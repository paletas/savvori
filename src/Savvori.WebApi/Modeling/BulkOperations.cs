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

    public async Task<BulkBatch> CreateAsync(double minCosine, CancellationToken ct = default)
    {
        var batch = new BulkBatch
        {
            Id = Guid.NewGuid(), Kind = "matches", Method = CosineMethod, Threshold = minCosine,
            Status = BulkBatchStatus.Running, Total = await Eligible(minCosine).CountAsync(ct), CreatedAt = Now
        };
        db.BulkBatches.Add(batch);
        await db.SaveChangesAsync(ct);
        return batch;
    }

    public async Task RunApplyAsync(Guid batchId, CancellationToken ct = default)
    {
        var batch = await db.BulkBatches.FirstAsync(b => b.Id == batchId, ct);
        var ids = await Eligible(batch.Threshold).OrderByDescending(c => c.Cosine).Select(c => c.Id).ToListAsync(ct);
        foreach (var id in ids)
        {
            var c = await db.MatchCandidates.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (c is null || c.Status != CandidateStatus.NeedsReview) { batch.Blocked++; continue; }

            var r = await applier.ApplyAsync(c, CosineMethod, manual: false, force: false, ct, batch.Id);
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
public sealed class CategoryBulkService(SavvoriDbContext db, TimeProvider time)
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
        var suggestions = await Eligible(batch.Threshold).OrderByDescending(s => s.Confidence).ToListAsync(ct);
        foreach (var s in suggestions)
        {
            var product = await db.Products.FirstOrDefaultAsync(p => p.Id == s.ProductId, ct);
            if (product is null || product.CategoryId is not null) { batch.Blocked++; continue; } // categorised meanwhile: never overwrite

            s.PreviousCategoryId = product.CategoryId;
            product.CategoryId = s.SuggestedCategoryId;
            product.CategorySource = null;
            s.Status = CategorySuggestionStatus.Applied;
            s.BatchId = batch.Id;
            s.DecidedAt = Now;
            batch.Applied++;
            if (batch.Applied % 100 == 0) await db.SaveChangesAsync(ct);
        }
        batch.Status = BulkBatchStatus.Done;
        batch.FinishedAt = Now;
        await db.SaveChangesAsync(ct);
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
