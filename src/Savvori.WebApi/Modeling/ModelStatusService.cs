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
    string JudgeModel);

public interface IModelStatusService
{
    Task<ModelStatus> GetAsync(CancellationToken ct = default);
}

public sealed class ModelStatusService(
    SavvoriDbContext db,
    ModelCircuitBreaker breaker,
    IStaleEmbeddingSource stale,
    IOptions<ModelOptions> options) : IModelStatusService
{
    public async Task<ModelStatus> GetAsync(CancellationToken ct = default)
    {
        var snap = breaker.Snapshot();

        var pending = db.ModelJobs.Where(j =>
            j.Status == ModelJobStatus.Pending || j.Status == ModelJobStatus.Running);
        var depth = await pending.CountAsync(ct);
        // Oldest = earliest creation among waiting jobs (SQLite has no DateTime Min aggregate on empty sets).
        var oldest = depth == 0
            ? (DateTime?)null
            : await pending.OrderBy(j => j.CreatedAt).Select(j => (DateTime?)j.CreatedAt).FirstOrDefaultAsync(ct);
        var dead = await db.ModelJobs.CountAsync(j => j.Status == ModelJobStatus.DeadLetter, ct);

        return new ModelStatus(
            options.Value.Enabled, snap.State.ToString(), snap.ConsecutiveFailures, snap.RetryAt,
            snap.LastSuccessAt, snap.LastErrorAt, snap.LastError,
            depth, oldest, dead, await stale.CountStaleAsync(ct),
            await db.StoreProducts.CountAsync(sp => sp.IsActive, ct),
            await db.StoreProductEmbeddings.CountAsync(e => e.ModelName == options.Value.EmbeddingModel, ct),
            await db.MatchCandidates.CountAsync(ct),
            options.Value.EmbeddingModel, options.Value.JudgeModel);
    }
}
