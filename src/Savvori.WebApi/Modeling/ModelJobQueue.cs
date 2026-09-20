using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Savvori.Shared;

namespace Savvori.WebApi.Modeling;

/// <summary>Does the work for one queued job. Must be idempotent: a job may run more than once.</summary>
public interface IModelJobHandler
{
    ModelJobType Type { get; }
    Task HandleAsync(ModelJob job, CancellationToken ct);
}

/// <summary>Durable, idempotent work queue over the <c>ModelJobs</c> table.</summary>
public sealed class ModelJobQueue(SavvoriDbContext db, IOptions<ModelOptions> options, TimeProvider time)
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>Adds a job unless an active (Pending/Running) one already exists for the same subject and input.</summary>
    public async Task<ModelJob> EnqueueAsync(
        ModelJobType type, Guid subjectId, string payloadHash, CancellationToken ct = default)
    {
        var existing = await FindActiveAsync(type, subjectId, payloadHash, ct);
        if (existing is not null) return existing;

        var now = Now;
        var job = new ModelJob
        {
            Id = Guid.NewGuid(), Type = type, SubjectId = subjectId, PayloadHash = payloadHash,
            Status = ModelJobStatus.Pending, NextAttemptAt = now, CreatedAt = now, UpdatedAt = now
        };
        db.ModelJobs.Add(job);
        try
        {
            await db.SaveChangesAsync(ct);
            return job;
        }
        catch (DbUpdateException)
        {
            // Lost a race with a concurrent enqueue (unique index on active rows): use theirs.
            db.Entry(job).State = EntityState.Detached;
            return await FindActiveAsync(type, subjectId, payloadHash, ct) ?? throw new InvalidOperationException(
                "Could not enqueue model job.");
        }
    }

    private Task<ModelJob?> FindActiveAsync(ModelJobType type, Guid subjectId, string payloadHash, CancellationToken ct) =>
        db.ModelJobs.FirstOrDefaultAsync(j =>
            j.Type == type && j.SubjectId == subjectId && j.PayloadHash == payloadHash &&
            (j.Status == ModelJobStatus.Pending || j.Status == ModelJobStatus.Running), ct);

    /// <summary>
    /// Claims up to <paramref name="batch"/> due jobs of the given types: Pending jobs whose time has come, and
    /// Running jobs whose lease expired (worker died mid-batch). Claiming counts as an attempt.
    /// </summary>
    public async Task<List<Guid>> ClaimDueAsync(IReadOnlyCollection<ModelJobType> types, int batch, CancellationToken ct = default)
    {
        var now = Now;
        var due = await db.ModelJobs
            .Where(j => types.Contains(j.Type) &&
                ((j.Status == ModelJobStatus.Pending && j.NextAttemptAt <= now) ||
                 (j.Status == ModelJobStatus.Running && j.LeaseExpiresAt <= now)))
            .OrderBy(j => j.NextAttemptAt)
            .Take(batch)
            .ToListAsync(ct);

        foreach (var job in due)
        {
            job.Status = ModelJobStatus.Running;
            job.Attempts++;
            job.LeaseExpiresAt = now.AddSeconds(options.Value.Queue.LeaseSeconds);
            job.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        return due.Select(j => j.Id).ToList();
    }

    public async Task<ModelJob?> GetAsync(Guid id, CancellationToken ct = default) =>
        await db.ModelJobs.FirstOrDefaultAsync(j => j.Id == id, ct);

    public async Task CompleteAsync(Guid id, CancellationToken ct = default)
    {
        var job = await GetAsync(id, ct);
        if (job is null) return;
        job.Status = ModelJobStatus.Done;
        job.LeaseExpiresAt = null;
        job.LastError = null;
        job.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Records a failure. A <paramref name="countAttempt"/> failure backs off exponentially with jitter and
    /// dead-letters after MaxAttempts. An uncounted one (the model is known to be down system-wide) is deferred
    /// to <paramref name="retryAt"/> without using up an attempt, so an outage never dead-letters healthy jobs.
    /// </summary>
    public async Task FailAsync(Guid id, string error, bool countAttempt, DateTime? retryAt = null, CancellationToken ct = default)
    {
        var job = await GetAsync(id, ct);
        if (job is null) return;
        var now = Now;
        job.LastError = Truncate(error);
        job.LeaseExpiresAt = null;
        job.UpdatedAt = now;

        if (!countAttempt)
        {
            job.Attempts = Math.Max(0, job.Attempts - 1);
            job.Status = ModelJobStatus.Pending;
            job.NextAttemptAt = retryAt ?? now.AddSeconds(options.Value.Breaker.CooldownSeconds);
        }
        else if (job.Attempts >= options.Value.Queue.MaxAttempts)
        {
            job.Status = ModelJobStatus.DeadLetter;
        }
        else
        {
            job.Status = ModelJobStatus.Pending;
            job.NextAttemptAt = now + ComputeBackoff(job.Attempts, options.Value.Queue, Random.Shared.NextDouble());
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Puts a claimed job back untouched (e.g. the breaker opened before it ran).</summary>
    public Task ReleaseAsync(Guid id, DateTime? retryAt, CancellationToken ct = default) =>
        FailAsync(id, "Released: model unavailable.", countAttempt: false, retryAt, ct);

    /// <summary>Moves every dead-lettered job back to Pending with a fresh attempt budget.</summary>
    public async Task<int> RequeueDeadLettersAsync(CancellationToken ct = default)
    {
        var now = Now;
        var dead = await db.ModelJobs.Where(j => j.Status == ModelJobStatus.DeadLetter).ToListAsync(ct);
        foreach (var job in dead)
        {
            job.Status = ModelJobStatus.Pending;
            job.Attempts = 0;
            job.NextAttemptAt = now;
            job.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        return dead.Count;
    }

    /// <summary>Exponential backoff (base * 2^(attempt-1), capped) with jitter in [50%, 100%] of that delay.</summary>
    public static TimeSpan ComputeBackoff(int attempt, ModelOptions.QueueOptions q, double jitter01)
    {
        var exp = Math.Min(q.MaxDelaySeconds, q.BaseDelaySeconds * Math.Pow(2, Math.Max(0, attempt - 1)));
        return TimeSpan.FromSeconds(exp * (0.5 + 0.5 * Math.Clamp(jitter01, 0, 1)));
    }

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500];
}
