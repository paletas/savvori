using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Quartz;
using Savvori.Shared;
using Savvori.WebApi.Scraping;

namespace Savvori.WebApi.Modeling;

/// <summary>One product's k-NN outcome: the winner's share of the similarity-weighted vote.</summary>
public sealed record KnnVote(Guid CategoryId, double Confidence, Guid? RunnerUp, int Neighbours);

public static class KnnVoting
{
    /// <summary>
    /// Similarity-weighted vote over labelled neighbours: each neighbour adds its cosine to its category's weight;
    /// confidence is the winning category's share of the total weight. Returns null when nothing votes.
    /// </summary>
    public static KnnVote? Tally(IEnumerable<(Guid Category, double Weight)> votes)
    {
        var weights = new Dictionary<Guid, double>();
        var n = 0;
        foreach (var (category, weight) in votes)
        {
            if (weight <= 0) continue;
            weights[category] = weights.GetValueOrDefault(category) + weight;
            n++;
        }
        if (n == 0) return null;
        var ranked = weights.OrderByDescending(kv => kv.Value).ToList();
        var total = ranked.Sum(kv => kv.Value);
        return new KnnVote(ranked[0].Key, ranked[0].Value / total, ranked.Count > 1 ? ranked[1].Key : null, n);
    }
}

/// <param name="AssignedByStoreCategory">Products categorised through a store-category decision you accepted earlier.</param>
/// <param name="Confident">Predictions at the confident level, waiting for a click (or bulk apply).</param>
public sealed record ClassifierRunResult(
    string? SkippedReason, int Targets, int AssignedByStoreCategory, int Confident, int ToReview,
    int NoSuggestion, int StringsProposed, int StringsMixed);

/// <summary>
/// Predicts categories for uncategorised canonical products by k-NN over the stored embeddings of already
/// categorised ones. Only ever touches products with no category, so existing (rule-based or manual) labels are never
/// overwritten. Makes no model call, but like matching it decides nothing while the model is unavailable, so the
/// rule-based <see cref="CategoryMapper"/> stays the only path in degraded mode.
/// </summary>
public sealed class CategoryClassifier(
    SavvoriDbContext db, EmbeddingIndex index, ModelCircuitBreaker breaker, IOptions<ModelOptions> options,
    TimeProvider time, ModelTelemetry telemetry)
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<ClassifierRunResult> RunAsync(CancellationToken ct = default)
    {
        var opts = options.Value;
        var o = opts.Categories;
        ClassifierRunResult Skipped(string why) => new(why, 0, 0, 0, 0, 0, 0, 0);
        if (!opts.Enabled) return Skipped("Model features are disabled.");
        if (!breaker.IsClosed) return Skipped("Model unavailable (degraded mode): categories come from the rule-based mapper only.");

        var snapshot = await index.RefreshAsync(ct);
        if (snapshot.Count == 0 || snapshot.Identity is null) return Skipped("No embeddings yet.");
        var identity = snapshot.Identity;

        // Listing -> canonical, for embedded listings only.
        var listings = await db.StoreProducts.AsNoTracking()
            .Where(sp => sp.IsActive && sp.CanonicalProductId != null)
            .Select(sp => new { sp.Id, Canonical = sp.CanonicalProductId!.Value }).ToListAsync(ct);
        var canonicalOf = listings.ToDictionary(l => l.Id, l => l.Canonical);
        var products = await db.Products.ToDictionaryAsync(p => p.Id, ct);

        // Never learn from the classifier's own decisions.
        var selfLabelled = (await db.CategorySuggestions.AsNoTracking()
            .Where(s => s.Status == CategorySuggestionStatus.Applied && (s.Method == "embedding-knn" || s.Method == "string-cache"))
            .Select(s => s.ProductId).ToListAsync(ct)).ToHashSet();
        var rejected = (await db.CategorySuggestions.AsNoTracking()
            .Where(s => s.Status == CategorySuggestionStatus.Rejected).Select(s => s.ProductId).ToListAsync(ct)).ToHashSet();

        var labelled = new List<(float[] Vector, Guid Category)>();
        var targetListings = new Dictionary<Guid, List<float[]>>(); // canonical -> its embedded listing vectors
        foreach (var entry in snapshot.Entries)
        {
            if (!canonicalOf.TryGetValue(entry.StoreProductId, out var canonical) || !products.TryGetValue(canonical, out var p)) continue;
            if (p.CategoryId is { } cat)
            {
                if (!selfLabelled.Contains(canonical)) labelled.Add((entry.Vector, cat));
            }
            else if (!rejected.Contains(canonical))
            {
                if (!targetListings.TryGetValue(canonical, out var list)) targetListings[canonical] = list = [];
                list.Add(entry.Vector);
            }
        }
        if (labelled.Count == 0) return Skipped("No categorised products to learn from.");

        // Vote per target canonical (parallel: targets x labelled dot products).
        var targets = targetListings.ToList();
        var votes = new List<(Guid Category, double Weight)>[targets.Count];
        Parallel.For(0, targets.Count, new ParallelOptions { CancellationToken = ct }, i =>
        {
            var all = new List<(Guid, double)>();
            foreach (var vector in targets[i].Value)
                all.AddRange(Nearest(vector, labelled, o.K, o.MinNeighbourCosine));
            votes[i] = all;
        });

        var existing = await db.CategorySuggestions.ToDictionaryAsync(s => s.ProductId, ct);
        var strings = await db.CategoryStringDecisions.ToDictionaryAsync(s => s.RawString, ct);
        int auto = 0, would = 0, review = 0, none = 0, stringsDecided = 0, stringsMixed = 0;

        // 1) Whole-string decisions: one decision per raw store-category string, reused for every product with it.
        var byString = new Dictionary<string, List<int>>();
        for (var i = 0; i < targets.Count; i++)
            if (products[targets[i].Key].Category is { Length: > 0 } raw &&
                ProductNormalizer.Normalize(raw) is { Length: > 0 } key)
            {
                if (!byString.TryGetValue(key, out var idx)) byString[key] = idx = [];
                idx.Add(i);
            }
        var decidedByString = new Dictionary<int, (Guid Category, double Confidence, int Neighbours)>();
        foreach (var (key, idx) in byString)
        {
            strings.TryGetValue(key, out var known);
            if (known is { Status: CategorySuggestionStatus.Applied, CategoryId: { } appliedCat })
            {
                foreach (var i in idx) decidedByString[i] = (appliedCat, known.Confidence, 0); // cached: not re-decided
                continue;
            }
            if (known is { Status: CategorySuggestionStatus.Rejected or CategorySuggestionStatus.Suggested }) continue;
            if (known is { Status: CategorySuggestionStatus.Mixed } && Math.Abs(idx.Count - known.Support) < Math.Max(1, known.Support / 4)) continue;
            if (idx.Count < o.MinStringSupport) continue;

            var vote = KnnVoting.Tally(idx.SelectMany(i => votes[i]));
            var row = known ?? new CategoryStringDecision { Id = Guid.NewGuid(), RawString = key, CreatedAt = Now };
            row.Support = idx.Count;
            row.Method = "embedding-knn";
            row.ModelName = identity.ModelName;
            row.ModelDigest = identity.ModelDigest;
            // A whole-string decision moves every product carrying the string, so it needs the auto-assign level of
            // agreement; anything less is treated as mixed and the products are decided one by one.
            if (vote is null || vote.Confidence < o.AutoAssignConfidence)
            {
                row.Status = CategorySuggestionStatus.Mixed;
                row.CategoryId = null;
                row.Confidence = vote?.Confidence ?? 0;
                stringsMixed++;
            }
            else
            {
                // Proposed only: it takes effect when you accept it ("Apply to all"), never on its own.
                row.CategoryId = vote.CategoryId;
                row.Confidence = vote.Confidence;
                row.Status = CategorySuggestionStatus.Suggested;
                stringsDecided++;
            }
            if (known is null) db.CategoryStringDecisions.Add(row);
        }

        // 2) Per-product decisions for everything not settled by a cached string.
        for (var i = 0; i < targets.Count; i++)
        {
            var canonical = targets[i].Key;
            var product = products[canonical];
            existing.TryGetValue(canonical, out var row);
            if (row is { Status: CategorySuggestionStatus.Rejected or CategorySuggestionStatus.Applied }) continue;

            if (decidedByString.TryGetValue(i, out var byStr))
            {
                Upsert(product, row, byStr.Category, null, byStr.Confidence, byStr.Neighbours, "string-cache", apply: true, identity);
                auto++;
                continue;
            }

            var vote = KnnVoting.Tally(votes[i]);
            if (vote is null || vote.Confidence < o.ReviewMinConfidence)
            {
                if (row is not null) db.CategorySuggestions.Remove(row); // stale suggestion that no longer holds up
                none++;
                continue;
            }
            Upsert(product, row, vote.CategoryId, vote.RunnerUp, vote.Confidence, vote.Neighbours, "embedding-knn", apply: false, identity);
            if (vote.Confidence >= o.AutoAssignConfidence) would++;
            else review++;
        }

        await db.SaveChangesAsync(ct);
        return new(null, targets.Count, auto, would, review, none, stringsDecided, stringsMixed);

        void Upsert(Product product, CategorySuggestion? row, Guid category, Guid? runnerUp, double confidence,
            int neighbours, string method, bool apply, ModelInfo id)
        {
            if (row is null)
            {
                row = new CategorySuggestion { Id = Guid.NewGuid(), ProductId = product.Id, CreatedAt = Now };
                db.CategorySuggestions.Add(row);
            }
            row.SuggestedCategoryId = category;
            row.RunnerUpCategoryId = runnerUp;
            row.Confidence = confidence;
            row.NeighbourCount = neighbours;
            row.Method = method;
            row.ModelName = id.ModelName;
            row.ModelDigest = id.ModelDigest;
            if (apply)
            {
                row.PreviousCategoryId = product.CategoryId;
                product.CategoryId = category;
                row.Status = CategorySuggestionStatus.Applied;
                row.DecidedAt = Now;
            }
            else row.Status = CategorySuggestionStatus.Suggested;
        }
    }

    /// <summary>Top-k labelled neighbours of a vector as (category, cosine) votes, ignoring weak ones.</summary>
    public static List<(Guid Category, double Weight)> Nearest(
        float[] vector, IReadOnlyList<(float[] Vector, Guid Category)> labelled, int k, double minCosine)
    {
        var best = new List<(Guid Category, double Weight)>(k + 1);
        foreach (var (v, category) in labelled)
        {
            var cos = IndexSnapshot.Dot(vector, v);
            if (cos < minCosine) continue;
            if (best.Count == k && cos <= best[^1].Weight) continue;
            var at = best.FindIndex(n => n.Weight < cos);
            best.Insert(at < 0 ? best.Count : at, (category, cos));
            if (best.Count > k) best.RemoveAt(best.Count - 1);
        }
        return best;
    }

    // ---- Human decisions --------------------------------------------------------------------------

    /// <summary>Accepts a suggestion: sets the category (only if the product still has none) and records it as manual.</summary>
    public async Task<(bool Ok, string? Error)> AcceptAsync(Guid suggestionId, CancellationToken ct = default)
    {
        var s = await db.CategorySuggestions.FirstOrDefaultAsync(x => x.Id == suggestionId, ct);
        if (s is null) return (false, "Suggestion not found.");
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == s.ProductId, ct);
        if (product is null) return (false, "Product no longer exists.");
        if (s.Status == CategorySuggestionStatus.Applied) return (false, "Already applied.");
        if (product.CategoryId is not null && product.CategoryId != s.SuggestedCategoryId)
            return (false, "The product was categorised in the meantime; nothing was changed.");
        s.PreviousCategoryId = product.CategoryId;
        product.CategoryId = s.SuggestedCategoryId;
        s.Status = CategorySuggestionStatus.Applied;
        s.Method = MatchApplier.ManualMethod;
        s.DecidedAt = Now;
        await db.SaveChangesAsync(ct);
        telemetry.CategoriesDecided.Add(1, new KeyValuePair<string, object?>("outcome", "accepted"));
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> RejectAsync(Guid suggestionId, CancellationToken ct = default)
    {
        var s = await db.CategorySuggestions.FirstOrDefaultAsync(x => x.Id == suggestionId, ct);
        if (s is null) return (false, "Suggestion not found.");
        if (s.Status == CategorySuggestionStatus.Applied) return (false, "This suggestion is applied; undo it first.");
        s.Status = CategorySuggestionStatus.Rejected;
        s.Method = MatchApplier.ManualMethod;
        s.DecidedAt = Now;
        await db.SaveChangesAsync(ct);
        telemetry.CategoriesDecided.Add(1, new KeyValuePair<string, object?>("outcome", "rejected"));
        return (true, null);
    }

    /// <summary>Undoes an applied category: restores the previous category (none) and rejects the suggestion.</summary>
    public async Task<(bool Ok, string? Error)> UndoAsync(Guid suggestionId, CancellationToken ct = default)
    {
        var s = await db.CategorySuggestions.FirstOrDefaultAsync(x => x.Id == suggestionId, ct);
        if (s is null || s.Status != CategorySuggestionStatus.Applied) return (false, "Nothing to undo.");
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == s.ProductId, ct);
        if (product is not null && product.CategoryId == s.SuggestedCategoryId) product.CategoryId = s.PreviousCategoryId;
        s.Status = CategorySuggestionStatus.Rejected;
        s.DecidedAt = Now;
        s.Note = "Undone.";
        await db.SaveChangesAsync(ct);
        telemetry.CategoriesDecided.Add(1, new KeyValuePair<string, object?>("outcome", "undone"));
        return (true, null);
    }

    /// <summary>
    /// Accepts a whole-string decision: every uncategorised product carrying the string gets the category.
    /// Products that already have one are left alone.
    /// </summary>
    public async Task<(bool Ok, string? Error, int Updated)> AcceptStringAsync(Guid decisionId, CancellationToken ct = default)
    {
        var d = await db.CategoryStringDecisions.FirstOrDefaultAsync(x => x.Id == decisionId, ct);
        if (d is null || d.CategoryId is null) return (false, "Decision not found.", 0);
        var normalisedTargets = await db.Products.Where(p => p.CategoryId == null && p.Category != null).ToListAsync(ct);
        var updated = 0;
        foreach (var p in normalisedTargets.Where(p => ProductNormalizer.Normalize(p.Category!) == d.RawString))
        {
            var row = await db.CategorySuggestions.FirstOrDefaultAsync(s => s.ProductId == p.Id, ct);
            if (row is { Status: CategorySuggestionStatus.Rejected }) continue;
            if (row is null)
            {
                row = new CategorySuggestion { Id = Guid.NewGuid(), ProductId = p.Id, CreatedAt = Now, ModelName = d.ModelName, ModelDigest = d.ModelDigest };
                db.CategorySuggestions.Add(row);
            }
            row.SuggestedCategoryId = d.CategoryId.Value;
            row.Confidence = d.Confidence;
            row.Method = MatchApplier.ManualMethod;
            row.PreviousCategoryId = p.CategoryId;
            row.Status = CategorySuggestionStatus.Applied;
            row.DecidedAt = Now;
            p.CategoryId = d.CategoryId;
            updated++;
        }
        d.Status = CategorySuggestionStatus.Applied;
        d.Method = MatchApplier.ManualMethod;
        d.DecidedAt = Now;
        await db.SaveChangesAsync(ct);
        if (updated > 0) telemetry.CategoriesDecided.Add(updated, new KeyValuePair<string, object?>("outcome", "accepted-bulk"));
        return (true, null, updated);
    }

    public async Task<bool> RejectStringAsync(Guid decisionId, CancellationToken ct = default)
    {
        var d = await db.CategoryStringDecisions.FirstOrDefaultAsync(x => x.Id == decisionId, ct);
        if (d is null) return false;
        d.Status = CategorySuggestionStatus.Rejected;
        d.Method = MatchApplier.ManualMethod;
        d.DecidedAt = Now;
        await db.SaveChangesAsync(ct);
        telemetry.CategoriesDecided.Add(1, new KeyValuePair<string, object?>("outcome", "rejected-bulk"));
        return true;
    }
}

/// <summary>Nightly, after matching: propose categories for uncategorised products. Skipped while off or degraded.</summary>
[DisallowConcurrentExecution]
public sealed class CategoryClassifierJob(
    IOptions<ModelOptions> options, IServiceScopeFactory scopes, ModelTelemetry telemetry, ILogger<CategoryClassifierJob> logger) : IJob
{
    private const string JobName = "category-classifier";

    public async Task Execute(IJobExecutionContext context)
    {
        if (!options.Value.Enabled) return;
        using var activity = telemetry.StartRunActivity(JobName);
        telemetry.RunsStarted.Add(1, new KeyValuePair<string, object?>("job.name", JobName));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var scope = scopes.CreateScope();
            var r = await scope.ServiceProvider.GetRequiredService<CategoryClassifier>().RunAsync(context.CancellationToken);
            logger.LogInformation(
                "Category classifier: skipped={Skipped}, {Targets} uncategorised, {Assigned} assigned through accepted store categories, " +
                "{Confident} confident suggestions, {Review} to review, {None} without suggestion, {Strings} store categories proposed, {Mixed} mixed.",
                r.SkippedReason, r.Targets, r.AssignedByStoreCategory, r.Confident, r.ToReview, r.NoSuggestion, r.StringsProposed, r.StringsMixed);
            if (r.AssignedByStoreCategory > 0) telemetry.CategoriesDecided.Add(r.AssignedByStoreCategory, new KeyValuePair<string, object?>("outcome", "assigned"));
            if (r.Confident > 0) telemetry.CategoriesDecided.Add(r.Confident, new KeyValuePair<string, object?>("outcome", "confident"));
            if (r.ToReview > 0) telemetry.CategoriesDecided.Add(r.ToReview, new KeyValuePair<string, object?>("outcome", "review"));
            if (r.NoSuggestion > 0) telemetry.CategoriesDecided.Add(r.NoSuggestion, new KeyValuePair<string, object?>("outcome", "none"));
            telemetry.RunsCompleted.Add(1, new KeyValuePair<string, object?>("job.name", JobName));
        }
        catch
        {
            telemetry.RunsFailed.Add(1, new KeyValuePair<string, object?>("job.name", JobName));
            throw;
        }
        finally
        {
            telemetry.RunDurationMs.Record(sw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("job.name", JobName));
        }
    }
}
