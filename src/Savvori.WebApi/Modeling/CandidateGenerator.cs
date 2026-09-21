using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Quartz;
using Savvori.Shared;

namespace Savvori.WebApi.Modeling;

public sealed record CandidateRunResult(
    int Indexed, int Considered, int RejectedSize, int RejectedBrand, int AlreadySameCanonical,
    int Added, int Updated, int Removed)
{
    public int Kept => Added + Updated;
}

/// <summary>
/// Turns stored embeddings into stored, unapplied match proposals. Needs no model call, so it also works in
/// degraded mode. A partially embedded catalogue simply yields fewer (still correct) candidates: only pairs
/// where BOTH products have a current-model embedding are considered.
/// </summary>
public sealed class CandidateGenerator(
    SavvoriDbContext db, EmbeddingIndex index, IOptions<ModelOptions> options, TimeProvider time)
{
    public async Task<CandidateRunResult> GenerateAsync(CancellationToken ct = default)
    {
        var opts = options.Value.Candidates;
        var snapshot = await index.RefreshAsync(ct);
        // Nothing embedded (yet, or the index could not be built): leave existing proposals alone rather than wipe them.
        if (snapshot.Count == 0 || snapshot.Identity is null) return new(0, 0, 0, 0, 0, 0, 0, 0);

        var facts = await db.StoreProducts.AsNoTracking().Where(sp => sp.IsActive)
            .Select(sp => new ListingFacts(sp.Id, sp.StoreChainId, sp.CanonicalProductId, sp.Name, sp.Brand, sp.SizeValue, sp.Unit))
            .ToDictionaryAsync(f => f.Id, ct);

        // Top-K per product from the other chains (brute force, parallel), THEN the hard filters.
        var neighbours = new List<Neighbor>[snapshot.Count];
        Parallel.For(0, snapshot.Count, new ParallelOptions { CancellationToken = ct },
            row => neighbours[row] = snapshot.TopNeighbors(row, opts.TopK, opts.MinCosine));

        var found = new Dictionary<(Guid, Guid), (double Cosine, bool SizeKnown, CandidateBrandCheck Brand)>();
        var seen = new HashSet<(Guid, Guid)>();
        int considered = 0, rejSize = 0, rejBrand = 0, same = 0;
        for (var row = 0; row < snapshot.Count; row++)
        {
            if (!facts.TryGetValue(snapshot.Entries[row].StoreProductId, out var a)) continue;
            foreach (var n in neighbours[row])
            {
                if (!facts.TryGetValue(snapshot.Entries[n.Row].StoreProductId, out var b)) continue;
                var key = a.Id.CompareTo(b.Id) < 0 ? (a.Id, b.Id) : (b.Id, a.Id);
                if (!seen.Add(key)) continue; // already seen from the other side
                considered++;

                if (a.CanonicalProductId is not null && a.CanonicalProductId == b.CanonicalProductId) { same++; continue; }
                var size = CandidateRules.CompareSizes(a, b, opts.SizeTolerance);
                if (size == SizeVerdict.Conflict) { rejSize++; continue; }
                var brand = CandidateRules.CompareBrands(a, b);
                if (brand == BrandVerdict.Conflict) { rejBrand++; continue; }

                found[key] = (n.Cosine, size == SizeVerdict.Compatible,
                    brand == BrandVerdict.Ok ? CandidateBrandCheck.Ok : CandidateBrandCheck.Unknown);
            }
        }

        var now = time.GetUtcNow().UtcDateTime;
        var identity = snapshot.Identity;
        var existing = await db.MatchCandidates.ToDictionaryAsync(c => (c.StoreProductAId, c.StoreProductBId), ct);
        int added = 0, updated = 0;
        foreach (var (key, v) in found)
        {
            if (existing.Remove(key, out var row))
            {
                // Decided pairs (applied, rejected, different variant) are history: never touched, never re-proposed.
                if (row.Status is CandidateStatus.Applied or CandidateStatus.Rejected or CandidateStatus.DifferentVariant)
                    continue;
                row.Cosine = v.Cosine; row.SizeKnown = v.SizeKnown; row.BrandCheck = v.Brand;
                row.ModelName = identity.ModelName; row.ModelDigest = identity.ModelDigest;
                updated++;
            }
            else
            {
                db.MatchCandidates.Add(new MatchCandidate
                {
                    Id = Guid.NewGuid(), StoreProductAId = key.Item1, StoreProductBId = key.Item2,
                    Cosine = v.Cosine, SizeKnown = v.SizeKnown, BrandCheck = v.Brand,
                    ModelName = identity.ModelName, ModelDigest = identity.ModelDigest, CreatedAt = now
                });
                added++;
            }
        }
        // Whatever is left no longer qualifies (text/size/brand/embedding changed, or already merged). Only undecided
        // proposals are removed: decisions (and above all rejections) must survive so a pair is never proposed again.
        var stale = existing.Values.Where(c =>
            c.Status is CandidateStatus.Proposed or CandidateStatus.PendingJudge or CandidateStatus.NeedsReview).ToList();
        db.MatchCandidates.RemoveRange(stale);
        await db.SaveChangesAsync(ct);

        return new(snapshot.Count, considered, rejSize, rejBrand, same, added, updated, stale.Count);
    }
}

/// <summary>Nightly: regenerate match proposals from stored embeddings. Skipped while the feature flag is off.</summary>
[DisallowConcurrentExecution]
public sealed class CandidateGenerationJob(
    IOptions<ModelOptions> options, IServiceScopeFactory scopes, ILogger<CandidateGenerationJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        if (!options.Value.Enabled) return;
        using var scope = scopes.CreateScope();
        var r = await scope.ServiceProvider.GetRequiredService<CandidateGenerator>().GenerateAsync(context.CancellationToken);
        logger.LogInformation(
            "Candidate generation: {Indexed} embedded products, {Considered} pairs considered, {Size} rejected on size, " +
            "{Brand} on brand, {Same} already same canonical; {Added} added, {Updated} updated, {Removed} removed.",
            r.Indexed, r.Considered, r.RejectedSize, r.RejectedBrand, r.AlreadySameCanonical, r.Added, r.Updated, r.Removed);
    }
}
