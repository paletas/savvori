using Microsoft.EntityFrameworkCore;
using Savvori.Shared;

namespace Savvori.WebApi.Modeling;

/// <summary>
/// Runs one bulk action at a time in the background (a bulk apply can take a minute, longer than a web request may
/// wait). Progress is written to the <see cref="BulkBatch"/> row; nothing else needs to poll a task.
/// </summary>
public sealed class BulkRunner(IServiceScopeFactory scopes, ILogger<BulkRunner> logger)
{
    private int _busy;
    private Task _last = Task.CompletedTask;

    public bool IsBusy => Volatile.Read(ref _busy) == 1;

    /// <summary>Completes when the most recently started run has finished (used by tests).</summary>
    public Task WhenIdle => _last;

    /// <summary>Starts <paramref name="work"/> unless another bulk run is in progress.</summary>
    public bool TryStart(Guid batchId, Func<IServiceProvider, Task> work)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return false;
        _last = Task.Run(async () =>
        {
            try
            {
                using var scope = scopes.CreateScope();
                await work(scope.ServiceProvider);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Bulk run {Batch} failed.", batchId);
                try
                {
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<SavvoriDbContext>();
                    var batch = await db.BulkBatches.FirstOrDefaultAsync(b => b.Id == batchId);
                    if (batch is not null)
                    {
                        batch.Status = BulkBatchStatus.Failed;
                        batch.Error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                        batch.FinishedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync();
                    }
                }
                catch (Exception inner) { logger.LogError(inner, "Could not record the failure of bulk run {Batch}.", batchId); }
            }
            finally
            {
                Volatile.Write(ref _busy, 0);
            }
        });
        return true;
    }
}

/// <summary>Bulk actions on the match review queue: apply every confident cosine suggestion, undo a whole run.</summary>
public sealed class MatchBulkService(SavvoriDbContext db, MatchApplier applier, TimeProvider time)
{
    public const string CosineMethod = "embedding-cosine";
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Suggestions a bulk apply would take: still waiting for review, suggested by the confident cosine tier (the judge is
    /// never bulk-applied), at or above the threshold, brand confirmed and not blocked by a safety rule.
    /// </summary>
    public IQueryable<MatchCandidate> Eligible(double minCosine) =>
        db.MatchCandidates.Where(c => c.Status == CandidateStatus.NeedsReview && c.Suggestion == CosineMethod &&
                                      c.Cosine >= minCosine && c.BrandCheck == CandidateBrandCheck.Ok && c.Note == null);

    public const string ExactMethod = "embedding-exact-name";

    /// <summary>One suggestion a bulk run would apply. <paramref name="Exact"/>: it is below the cosine threshold and only qualifies because the names say the same thing.</summary>
    public sealed record Pick(Guid Id, double Cosine, bool Exact);

    /// <summary>
    /// Everything a run at these settings would apply. Above <paramref name="minCosine"/>: the eligible suggestions
    /// whose names do not describe different variants. With <paramref name="exactFloor"/> (lower than the threshold):
    /// also pairs down to that cosine whose names are identical once brand, size and filler words are removed, the
    /// one case where a lower cosine is still very likely the same product. The judge is never involved.
    /// </summary>
    public async Task<List<Pick>> PickAsync(double minCosine, double? exactFloor = null, CancellationToken ct = default)
    {
        var strict = await Eligible(minCosine)
            .Select(c => new { c.Id, c.Cosine, c.StoreProductAId, c.StoreProductBId }).ToListAsync(ct);
        var exact = exactFloor is { } floor && floor < minCosine
            ? await db.MatchCandidates.Where(c =>
                    c.Status == CandidateStatus.NeedsReview && c.Note == null && c.BrandCheck == CandidateBrandCheck.Ok &&
                    c.SizeKnown && c.Cosine >= floor && c.Cosine < minCosine && c.JudgeVerdict != JudgeVerdict.No)
                .Select(c => new { c.Id, c.Cosine, c.StoreProductAId, c.StoreProductBId }).ToListAsync(ct)
            : [];

        var listings = new Dictionary<Guid, (string Name, string? Brand)>();
        foreach (var chunk in strict.Concat(exact).SelectMany(c => new[] { c.StoreProductAId, c.StoreProductBId }).Distinct().Chunk(500))
            foreach (var p in await db.StoreProducts.AsNoTracking().Where(sp => chunk.Contains(sp.Id))
                         .Select(sp => new { sp.Id, sp.Name, sp.Brand }).ToListAsync(ct))
                listings[p.Id] = (p.Name, p.Brand);

        VariantVerdict? Verdict(Guid a, Guid b) =>
            listings.TryGetValue(a, out var x) && listings.TryGetValue(b, out var y)
                ? VariantGuard.Compare(x.Name, x.Brand, y.Name, y.Brand) : null;

        var picks = new List<Pick>();
        picks.AddRange(strict.Where(c => Verdict(c.StoreProductAId, c.StoreProductBId) is { Conflict: false })
            .Select(c => new Pick(c.Id, c.Cosine, false)));
        picks.AddRange(exact.Where(c => Verdict(c.StoreProductAId, c.StoreProductBId) is { Identical: true })
            .Select(c => new Pick(c.Id, c.Cosine, true)));
        return picks.OrderByDescending(p => p.Cosine).ToList();
    }

    public async Task<BulkBatch> CreateAsync(double minCosine, double? exactFloor = null, CancellationToken ct = default)
    {
        var batch = new BulkBatch
        {
            Id = Guid.NewGuid(), Kind = "matches", Method = CosineMethod, Threshold = minCosine,
            Status = BulkBatchStatus.Running, Total = (await PickAsync(minCosine, exactFloor, ct)).Count, CreatedAt = Now
        };
        db.BulkBatches.Add(batch);
        await db.SaveChangesAsync(ct);
        return batch;
    }

    public async Task RunApplyAsync(Guid batchId, double? exactFloor = null, CancellationToken ct = default)
    {
        var batch = await db.BulkBatches.FirstAsync(b => b.Id == batchId, ct);
        var picks = await PickAsync(batch.Threshold, exactFloor, ct);
        foreach (var pick in picks)
        {
            var c = await db.MatchCandidates.FirstOrDefaultAsync(x => x.Id == pick.Id, ct);
            if (c is null || c.Status != CandidateStatus.NeedsReview) { batch.Blocked++; continue; }

            var r = await applier.ApplyAsync(c, pick.Exact ? ExactMethod : CosineMethod, manual: false, force: false, ct, batch.Id, guardVariants: true);
            if (r.Succeeded) batch.Applied++;
            else
            {
                c.Note = r.Reason; // stays in the queue with the reason (same-chain, EAN, manual protection, ...)
                batch.Blocked++;
            }
            if ((batch.Applied + batch.Blocked) % 25 == 0) await db.SaveChangesAsync(ct);
        }
        batch.Status = BulkBatchStatus.Done;
        batch.FinishedAt = Now;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Undoes every merge of a run, newest first. Pairs go back to the review queue, not to "rejected".</summary>
    public async Task RunUndoAsync(Guid batchId, CancellationToken ct = default)
    {
        var batch = await db.BulkBatches.FirstAsync(b => b.Id == batchId, ct);
        var candidateIds = await db.MatchMerges.Where(m => m.BatchId == batchId && m.UndoneAt == null)
            .OrderByDescending(m => m.AppliedAt).Select(m => m.CandidateId).ToListAsync(ct);
        foreach (var id in candidateIds)
        {
            var r = await applier.UndoBulkItemAsync(id, ct);
            if (r.Succeeded) batch.Undone++;
            if (batch.Undone % 25 == 0) await db.SaveChangesAsync(ct);
        }
        batch.Status = BulkBatchStatus.Undone;
        batch.UndoneAt = Now;
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Bulk actions on the category review queue: apply every confident prediction, undo a whole run.</summary>
public sealed class CategoryBulkService(
    SavvoriDbContext db, TimeProvider time, ModelTelemetry telemetry, ICategoryJudge judge, ILogger<CategoryBulkService> logger)
{
    public const string KnnMethod = "embedding-knn";
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>Predictions waiting for review at or above the threshold (whole-string proposals are decided one by one).</summary>
    public IQueryable<CategorySuggestion> Eligible(double minConfidence) =>
        db.CategorySuggestions.Where(s => s.Status == CategorySuggestionStatus.Suggested && s.Method == KnnMethod &&
                                          s.Confidence >= minConfidence);

    public async Task<BulkBatch> CreateAsync(double minConfidence, CancellationToken ct = default)
    {
        var batch = new BulkBatch
        {
            Id = Guid.NewGuid(), Kind = "categories", Method = KnnMethod, Threshold = minConfidence,
            Status = BulkBatchStatus.Running, Total = await Eligible(minConfidence).CountAsync(ct), CreatedAt = Now
        };
        db.BulkBatches.Add(batch);
        await db.SaveChangesAsync(ct);
        return batch;
    }

    public async Task RunApplyAsync(Guid batchId, CancellationToken ct = default)
    {
        var batch = await db.BulkBatches.FirstAsync(b => b.Id == batchId, ct);
        var suggestions = await Eligible(batch.Threshold)
            .Select(s => new { Suggestion = s, ProductName = s.Product.Name, CategoryName = s.SuggestedCategory.Name })
            .OrderByDescending(s => s.Suggestion.Confidence).ToListAsync(ct);
        var processed = 0;
        foreach (var row in suggestions)
        {
            var s = row.Suggestion;
            var product = await db.Products.FirstOrDefaultAsync(p => p.Id == s.ProductId, ct);
            if (product is null || product.CategoryId is not null)
            {
                // Categorised meanwhile: never overwrite. The suggestion is moot, so drop it; left in the queue it would be
                // counted as eligible (and skipped again) by every later run.
                db.CategorySuggestions.Remove(s);
                batch.Blocked++;
            }
            else if (CategoryGuard.Suspicious(row.ProductName, row.CategoryName))
            {
                // A likely literal-word collision (an object, not the ingredient the word suggests): stays in the
                // queue with a note instead of being assigned, even at full confidence.
                s.Note = "Held back: looks like a literal word match, not the product.";
                batch.Blocked++;
            }
            else
            {
                JudgeVerdict verdict;
                string? unavailableNote = null;
                try
                {
                    verdict = await judge.JudgeAsync(
                        new CategoryJudgeItem(row.ProductName, product.Brand, product.Category, row.CategoryName), ct);
                }
                catch (ModelUnavailableException ex)
                {
                    // Consistent with the rest of the model pipeline: when the model is down, nothing auto-applies.
                    // The shared circuit breaker fails fast on repeated failures and recovers after its cooldown,
                    // so this is retried per item rather than latched off for the rest of the run.
                    logger.LogWarning("Category judge unavailable for '{Product}': {Error}", row.ProductName, ex.Message);
                    verdict = JudgeVerdict.Unclear;
                    unavailableNote = "Held back: category judge was unavailable.";
                }
                catch (ModelResponseException ex)
                {
                    logger.LogWarning("Category judge gave a bad response for '{Product}': {Error}", row.ProductName, ex.Message);
                    verdict = JudgeVerdict.Unclear;
                    unavailableNote = "Held back: category judge was unavailable.";
                }

                if (verdict != JudgeVerdict.Yes)
                {
                    s.Note = unavailableNote ??
                             "Held back: model judge does not think this product belongs in the suggested category.";
                    batch.Blocked++;
                }
                else
                {
                    s.PreviousCategoryId = product.CategoryId;
                    product.CategoryId = s.SuggestedCategoryId;
                    product.CategorySource = null;
                    s.Status = CategorySuggestionStatus.Applied;
                    s.BatchId = batch.Id;
                    s.DecidedAt = Now;
                    batch.Applied++;
                }
            }
            processed++;
            if (processed % 100 == 0) await db.SaveChangesAsync(ct);
        }
        batch.Status = BulkBatchStatus.Done;
        batch.FinishedAt = Now;
        await db.SaveChangesAsync(ct);
        if (batch.Applied > 0) telemetry.CategoriesDecided.Add(batch.Applied, new KeyValuePair<string, object?>("outcome", "accepted-bulk"));
    }

    /// <summary>Removes the categories a run assigned (only where the product still has that category) and re-queues them.</summary>
    public async Task RunUndoAsync(Guid batchId, CancellationToken ct = default)
    {
        var batch = await db.BulkBatches.FirstAsync(b => b.Id == batchId, ct);
        var suggestions = await db.CategorySuggestions.Where(s => s.BatchId == batchId && s.Status == CategorySuggestionStatus.Applied).ToListAsync(ct);
        foreach (var s in suggestions)
        {
            var product = await db.Products.FirstOrDefaultAsync(p => p.Id == s.ProductId, ct);
            if (product is not null && product.CategoryId == s.SuggestedCategoryId) product.CategoryId = s.PreviousCategoryId;
            s.Status = CategorySuggestionStatus.Suggested;
            s.BatchId = null;
            s.DecidedAt = null;
            s.Note = "Bulk run undone.";
            batch.Undone++;
        }
        batch.Status = BulkBatchStatus.Undone;
        batch.UndoneAt = Now;
        await db.SaveChangesAsync(ct);
    }
}
