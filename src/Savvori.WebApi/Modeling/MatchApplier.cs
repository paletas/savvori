using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Savvori.Shared;

namespace Savvori.WebApi.Modeling;

public enum MatchTier { AutoAccept, Judge, Review, Leave }

/// <summary>The tier rules of the matching pipeline (pure; all thresholds come from configuration).</summary>
public static class MatchPolicy
{
    /// <summary>
    /// Tier B: cosine at or above the accept threshold (stricter when a size is unknown) AND the brand check passed.
    /// Tier C: from the judge lower bound up, including confident pairs whose brand is unknown.
    /// Tier D: from ReviewMinCosine up. Anything lower stays an unqueued proposal.
    /// </summary>
    public static MatchTier Decide(double cosine, bool sizeKnown, CandidateBrandCheck brand, ModelOptions.MatchingOptions o)
    {
        var accept = sizeKnown ? o.AcceptCosineSizeKnown : o.AcceptCosineSizeUnknown;
        var judgeLower = sizeKnown ? o.JudgeLowerSizeKnown : o.JudgeLowerSizeUnknown;
        if (cosine >= accept && (brand == CandidateBrandCheck.Ok || !o.AutoAcceptRequiresBrandOk)) return MatchTier.AutoAccept;
        if (cosine >= judgeLower) return MatchTier.Judge;
        if (cosine >= o.ReviewMinCosine) return MatchTier.Review;
        return MatchTier.Leave;
    }
}

public enum ApplyOutcome { Applied, AlreadyTogether, Blocked }

public sealed record ApplyResult(ApplyOutcome Outcome, string? Reason = null)
{
    public bool Succeeded => Outcome != ApplyOutcome.Blocked;
}

/// <summary>
/// Links two store products to one canonical product, safely and reversibly. Never merges canonicals that both
/// have active prices from the same chain (usually different packs) or carry different EANs unless forced by a
/// human, never touches manually matched products on behalf of a model, and records everything needed to undo.
/// </summary>
public sealed class MatchApplier(SavvoriDbContext db, TimeProvider time)
{
    public const string ManualMethod = "manual-review";

    private sealed record ProductSnapshot(
        Guid Id, string Name, string? Brand, string? Category, Guid? CategoryId, string? NormalizedName,
        string? EAN, ProductUnit Unit, decimal? SizeValue, string? ImageUrl);

    private sealed record MovedStoreProduct(
        Guid Id, Guid? PrevCanonicalId, MatchStatus PrevStatus, string? PrevMethod, DateTime? PrevMatchedAt);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <param name="method">Recorded on the moved products and the candidate ("embedding-cosine", "embedding-judge", <see cref="ManualMethod"/>).</param>
    /// <param name="manual">A human decision: may move manually matched products and marks the result ManualMatched.</param>
    /// <param name="force">A human confirmed a merge the safety rules would otherwise block.</param>
    public async Task<ApplyResult> ApplyAsync(MatchCandidate c, string method, bool manual, bool force, CancellationToken ct = default)
    {
        var a = await db.StoreProducts.FirstOrDefaultAsync(sp => sp.Id == c.StoreProductAId, ct);
        var b = await db.StoreProducts.FirstOrDefaultAsync(sp => sp.Id == c.StoreProductBId, ct);
        if (a is null || b is null) return new(ApplyOutcome.Blocked, "A product no longer exists.");
        if (!a.IsActive || !b.IsActive) return new(ApplyOutcome.Blocked, "A product is no longer listed by its store.");

        if (a.CanonicalProductId is not null && a.CanonicalProductId == b.CanonicalProductId)
        {
            MarkApplied(c, method);
            return new(ApplyOutcome.AlreadyTogether);
        }
        if (!manual && (a.MatchStatus == MatchStatus.ManualMatched || b.MatchStatus == MatchStatus.ManualMatched))
            return new(ApplyOutcome.Blocked, "A product was matched manually; only you can change that.");
        if (a.CanonicalProductId is null && b.CanonicalProductId is null)
            return new(ApplyOutcome.Blocked, "Neither product has a canonical product yet.");

        var moved = new List<MovedStoreProduct>();
        var movedItems = new List<Guid>();
        Product survivor;
        Product? retired = null;
        Guid? survivorPrevCategory = null;

        if (a.CanonicalProductId is null || b.CanonicalProductId is null)
        {
            // One side has no canonical: simply link it to the other's.
            var loose = a.CanonicalProductId is null ? a : b;
            var anchor = a.CanonicalProductId is null ? b : a;
            survivor = await db.Products.FirstAsync(p => p.Id == anchor.CanonicalProductId, ct);
            var existing = await db.StoreProducts.Where(sp => sp.CanonicalProductId == survivor.Id).ToListAsync(ct);
            if (!force && existing.Any(sp => sp.IsActive && sp.StoreChainId == loose.StoreChainId))
                return new(ApplyOutcome.Blocked, "The other product's canonical already has a price from this chain.");
            Move(loose, survivor.Id, method, manual, moved);
        }
        else
        {
            var canonA = await db.Products.FirstAsync(p => p.Id == a.CanonicalProductId, ct);
            var canonB = await db.Products.FirstAsync(p => p.Id == b.CanonicalProductId, ct);
            var groupA = await db.StoreProducts.Where(sp => sp.CanonicalProductId == canonA.Id).ToListAsync(ct);
            var groupB = await db.StoreProducts.Where(sp => sp.CanonicalProductId == canonB.Id).ToListAsync(ct);

            if (!manual && (groupA.Concat(groupB).Any(sp => sp.MatchStatus == MatchStatus.ManualMatched)))
                return new(ApplyOutcome.Blocked, "One of the canonical products contains a manually matched product.");
            if (!force && !string.IsNullOrEmpty(canonA.EAN) && !string.IsNullOrEmpty(canonB.EAN) && canonA.EAN != canonB.EAN)
                return new(ApplyOutcome.Blocked, $"The canonical products have different EANs ({canonA.EAN} vs {canonB.EAN}).");
            var chainsA = groupA.Where(sp => sp.IsActive).Select(sp => sp.StoreChainId).ToHashSet();
            var overlap = groupB.Where(sp => sp.IsActive && chainsA.Contains(sp.StoreChainId)).ToList();
            if (!force && overlap.Count > 0)
                return new(ApplyOutcome.Blocked,
                    "Both canonical products already have prices from the same chain: probably different packs.");

            // Survivor: the one with more active listings, then the one with an EAN, then the lower id (stable).
            var activeA = groupA.Count(sp => sp.IsActive);
            var activeB = groupB.Count(sp => sp.IsActive);
            var eanA = string.IsNullOrEmpty(canonA.EAN) ? 0 : 1;
            var eanB = string.IsNullOrEmpty(canonB.EAN) ? 0 : 1;
            var aWins = activeA != activeB ? activeA > activeB
                      : eanA != eanB ? eanA > eanB
                      : canonA.Id.CompareTo(canonB.Id) < 0;
            survivor = aWins ? canonA : canonB;
            retired = aWins ? canonB : canonA;
            var retiredGroup = aWins ? groupB : groupA;

            foreach (var sp in retiredGroup) Move(sp, survivor.Id, method, manual, moved);

            var items = await db.ShoppingListItems.Where(i => i.ProductId == retired.Id).ToListAsync(ct);
            foreach (var item in items) { item.ProductId = survivor.Id; movedItems.Add(item.Id); }

            survivorPrevCategory = survivor.CategoryId;
            survivor.CategoryId ??= retired.CategoryId;
        }

        // A human decision protects the pair itself too, so no later job can move either product.
        if (manual)
            foreach (var sp in new[] { a, b }.Where(sp => moved.All(m => m.Id != sp.Id)))
                Move(sp, sp.CanonicalProductId!.Value, method, manual, moved);

        var merge = new MatchMerge
        {
            Id = Guid.NewGuid(), CandidateId = c.Id, SurvivorProductId = survivor.Id,
            RetiredProductId = retired?.Id,
            RetiredProductJson = retired is null ? null : JsonSerializer.Serialize(new ProductSnapshot(
                retired.Id, retired.Name, retired.Brand, retired.Category, retired.CategoryId, retired.NormalizedName,
                retired.EAN, retired.Unit, retired.SizeValue, retired.ImageUrl)),
            SurvivorPreviousCategoryId = survivorPrevCategory,
            MovedStoreProductsJson = JsonSerializer.Serialize(moved),
            MovedListItemsJson = JsonSerializer.Serialize(movedItems),
            Method = method, AppliedAt = Now
        };
        db.MatchMerges.Add(merge);
        // Everything moved off the retired canonical first, so deleting it cannot cascade into anything.
        if (retired is not null)
        {
            await db.SaveChangesAsync(ct);
            db.Products.Remove(retired);
        }
        MarkApplied(c, method);
        await db.SaveChangesAsync(ct);
        return new(ApplyOutcome.Applied);
    }

    /// <summary>
    /// A warning a reviewer should see before accepting (the same rules that block automatic merges and need
    /// "force"), or null when the merge has no known conflict. Read-only.
    /// </summary>
    public async Task<string?> PreviewConflictAsync(Guid storeProductA, Guid storeProductB, CancellationToken ct = default)
    {
        var a = await db.StoreProducts.AsNoTracking().FirstOrDefaultAsync(sp => sp.Id == storeProductA, ct);
        var b = await db.StoreProducts.AsNoTracking().FirstOrDefaultAsync(sp => sp.Id == storeProductB, ct);
        if (a?.CanonicalProductId is not { } ca || b?.CanonicalProductId is not { } cb || ca == cb) return null;

        var canonA = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == ca, ct);
        var canonB = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == cb, ct);
        if (canonA is null || canonB is null) return null;
        if (!string.IsNullOrEmpty(canonA.EAN) && !string.IsNullOrEmpty(canonB.EAN) && canonA.EAN != canonB.EAN)
            return $"The canonical products have different EANs ({canonA.EAN} vs {canonB.EAN}).";

        var chainsA = await db.StoreProducts.Where(sp => sp.CanonicalProductId == ca && sp.IsActive)
            .Select(sp => sp.StoreChainId).ToListAsync(ct);
        var clash = await db.StoreProducts.AnyAsync(
            sp => sp.CanonicalProductId == cb && sp.IsActive && chainsA.Contains(sp.StoreChainId), ct);
        return clash ? "Both already have prices from the same chain: probably different packs." : null;
    }

    private void Move(StoreProduct sp, Guid toCanonical, string method, bool manual, List<MovedStoreProduct> log)
    {
        log.Add(new(sp.Id, sp.CanonicalProductId, sp.MatchStatus, sp.MatchMethod, sp.MatchedAt));
        sp.CanonicalProductId = toCanonical;
        sp.MatchStatus = manual ? MatchStatus.ManualMatched : MatchStatus.AutoMatched;
        sp.MatchMethod = method;
        sp.MatchedAt = Now;
    }

    private void MarkApplied(MatchCandidate c, string method)
    {
        c.Status = CandidateStatus.Applied;
        c.Method = method;
        c.Suggestion = null;
        c.DecidedAt = Now;
        c.Note = null;
    }

    /// <summary>Reverts an applied merge (restores the retired canonical, store products and list items) and rejects the pair.</summary>
    public async Task<ApplyResult> UndoAsync(Guid candidateId, CancellationToken ct = default)
    {
        var candidate = await db.MatchCandidates.FirstOrDefaultAsync(c => c.Id == candidateId, ct);
        var merge = await db.MatchMerges.Where(m => m.CandidateId == candidateId && m.UndoneAt == null)
            .OrderByDescending(m => m.AppliedAt).FirstOrDefaultAsync(ct);
        if (candidate is null || merge is null) return new(ApplyOutcome.Blocked, "Nothing to undo for this pair.");

        var survivor = await db.Products.FirstOrDefaultAsync(p => p.Id == merge.SurvivorProductId, ct);
        if (survivor is null)
            return new(ApplyOutcome.Blocked, "The merged product was itself merged later; undo that merge first.");

        ProductSnapshot? snapshot = merge.RetiredProductJson is null
            ? null : JsonSerializer.Deserialize<ProductSnapshot>(merge.RetiredProductJson);
        if (snapshot is not null)
        {
            if (await db.Products.AnyAsync(p => p.Id == snapshot.Id, ct))
                return new(ApplyOutcome.Blocked, "The retired product id is already in use.");
            db.Products.Add(new Product
            {
                Id = snapshot.Id, Name = snapshot.Name, Brand = snapshot.Brand, Category = snapshot.Category,
                CategoryId = snapshot.CategoryId, NormalizedName = snapshot.NormalizedName, EAN = snapshot.EAN,
                Unit = snapshot.Unit, SizeValue = snapshot.SizeValue, ImageUrl = snapshot.ImageUrl
            });
            await db.SaveChangesAsync(ct); // exists before anything points back at it
        }

        foreach (var m in JsonSerializer.Deserialize<List<MovedStoreProduct>>(merge.MovedStoreProductsJson) ?? [])
        {
            var sp = await db.StoreProducts.FirstOrDefaultAsync(x => x.Id == m.Id, ct);
            if (sp is null || sp.CanonicalProductId != survivor.Id) continue; // moved again since: leave it
            sp.CanonicalProductId = m.PrevCanonicalId;
            sp.MatchStatus = m.PrevStatus;
            sp.MatchMethod = m.PrevMethod;
            sp.MatchedAt = m.PrevMatchedAt;
        }
        if (snapshot is not null)
            foreach (var id in JsonSerializer.Deserialize<List<Guid>>(merge.MovedListItemsJson) ?? [])
            {
                var item = await db.ShoppingListItems.FirstOrDefaultAsync(i => i.Id == id, ct);
                if (item is not null && item.ProductId == survivor.Id) item.ProductId = snapshot.Id;
            }
        if (snapshot is not null && survivor.CategoryId == snapshot.CategoryId)
            survivor.CategoryId = merge.SurvivorPreviousCategoryId;

        merge.UndoneAt = Now;
        candidate.Status = CandidateStatus.Rejected;
        candidate.Method = ManualMethod;
        candidate.Suggestion = null;
        candidate.DecidedAt = Now;
        candidate.Note = "Merge undone.";
        await db.SaveChangesAsync(ct);
        return new(ApplyOutcome.Applied);
    }
}
