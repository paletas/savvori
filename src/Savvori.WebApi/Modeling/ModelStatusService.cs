using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Savvori.Shared;

namespace Savvori.WebApi.Modeling;

public sealed record ModelStatus(
    bool Enabled,
    string BreakerState,
    int ConsecutiveFailures,
    DateTime? BreakerRetryAt,
    DateTime? LastSuccessAt,
    DateTime? LastErrorAt,
    string? LastError,
    int QueueDepth,
    DateTime? OldestPendingAt,
    int DeadLetterCount,
    int StaleEmbeddings,
    int ActiveProducts,
    int EmbeddedProducts,
    int Candidates,
    string EmbeddingModel,
    string CategoryModel,
    string PairJudgeModel,
    string TranslatorModel,
    IReadOnlyList<BreakerStatus> Breakers,
    decimal SpendTodayUsd,
    decimal Spend30dUsd,
    IReadOnlyList<TaskSpend> SpendByTask);

public sealed record BreakerStatus(string Backend, string State, int ConsecutiveFailures, DateTime? RetryAt, string? LastError);

/// <summary>Spend per task (embed | category | translate | pair | probe); Calls30d counts every row, failed attempts and probes included.</summary>
public sealed record TaskSpend(string Task, decimal TodayUsd, decimal Last30dUsd, int Calls30d);

public interface IModelStatusService
{
    Task<ModelStatus> GetAsync(CancellationToken ct = default);
}

public sealed class ModelStatusService(
    SavvoriDbContext db,
    ModelBreakers breakers,
    IStaleEmbeddingSource stale,
    IOptions<ModelOptions> options,
    TimeProvider time) : IModelStatusService
{
    public async Task<ModelStatus> GetAsync(CancellationToken ct = default)
    {
        // Legacy flat fields = Gateway breaker; see Breakers.
        var snap = breakers.For(ModelBackend.Gateway).Snapshot();

        var pending = db.ModelJobs.Where(j =>
            j.Status == ModelJobStatus.Pending || j.Status == ModelJobStatus.Running);
        var depth = await pending.CountAsync(ct);
        // Oldest = earliest creation among waiting jobs (SQLite has no DateTime Min aggregate on empty sets).
        var oldest = depth == 0
            ? (DateTime?)null
            : await pending.OrderBy(j => j.CreatedAt).Select(j => (DateTime?)j.CreatedAt).FirstOrDefaultAsync(ct);
        var dead = await db.ModelJobs.CountAsync(j => j.Status == ModelJobStatus.DeadLetter, ct);

        var now = time.GetUtcNow().UtcDateTime;
        var today = now.Date;
        var since30 = now.AddDays(-30);
        // Pruned to 90 days, so the 30-day window is small: group in memory (SQLite cannot sum decimals server-side).
        var rows = (await db.ModelCalls.Where(c => c.At >= since30)
            .Select(c => new { c.At, c.Task, c.CostUsd }).ToListAsync(ct));
        var byTask = rows.GroupBy(r => r.Task)
            .Select(g => new TaskSpend(g.Key,
                g.Where(r => r.At >= today).Sum(r => r.CostUsd ?? 0m),
                g.Sum(r => r.CostUsd ?? 0m), g.Count()))
            .OrderByDescending(t => t.Last30dUsd).ThenBy(t => t.Task, StringComparer.Ordinal)
            .ToList();
        var breakerList = breakers.All.Select(b =>
        {
            var s = b.Breaker.Snapshot();
            return new BreakerStatus(ModelBackends.Tag(b.Backend), s.State.ToString(), s.ConsecutiveFailures, s.RetryAt, s.LastError);
        }).ToList();

        return new ModelStatus(
            options.Value.Enabled, snap.State.ToString(), snap.ConsecutiveFailures, snap.RetryAt,
            snap.LastSuccessAt, snap.LastErrorAt, snap.LastError,
            depth, oldest, dead, await stale.CountStaleAsync(ct),
            await db.StoreProducts.CountAsync(sp => sp.IsActive, ct),
            await db.StoreProductEmbeddings.CountAsync(e => e.ModelName == options.Value.EmbeddingModel, ct),
            await db.MatchCandidates.CountAsync(ct),
            options.Value.EmbeddingModel, options.Value.CategoryModel, options.Value.PairJudgeModel, options.Value.TranslatorModel,
            breakerList, byTask.Sum(t => t.TodayUsd), byTask.Sum(t => t.Last30dUsd), byTask);
    }
}
