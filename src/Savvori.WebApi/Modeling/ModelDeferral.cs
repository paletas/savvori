using System.Collections.Concurrent;

namespace Savvori.WebApi.Modeling;

/// <summary>How long a job waits after a 429. Retry-After is only ever a lower bound.</summary>
public static class ModelDeferral
{
    /// <summary>
    /// Quota (monthly key budget): <c>max(RetryAfter, floor)</c>, not capped (the HTTP layer already clamps to 31 days).
    /// Routing / Proxy: <c>max(RetryAfter, floor) * 2^min(prior, 6)</c> with up to 25% jitter, capped at
    /// <c>Queue.MaxDelaySeconds</c>, so a gateway-wide cooldown many hours out is re-checked hourly.
    /// </summary>
    public static TimeSpan Compute(ThrottleKind kind, TimeSpan retryAfter, int priorDeferrals, ModelOptions.QueueOptions q, double jitter01)
    {
        var baseSeconds = Math.Max(retryAfter.TotalSeconds, q.ThrottleFloorSeconds);
        if (kind == ThrottleKind.Quota) return TimeSpan.FromSeconds(baseSeconds);

        var delay = baseSeconds * Math.Pow(2, Math.Min(Math.Max(0, priorDeferrals), 6))
                    * (1 + 0.25 * Math.Clamp(jitter01, 0, 1));
        return TimeSpan.FromSeconds(Math.Min(delay, q.MaxDelaySeconds));
    }
}

/// <summary>
/// Consecutive Routing/Proxy deferrals per job id, in memory only (lost on restart, which just starts the
/// back-off again at the floor).
/// </summary>
public sealed class ThrottleTracker
{
    private readonly ConcurrentDictionary<Guid, int> _counts = new();

    public int Prior(Guid jobId) => _counts.TryGetValue(jobId, out var n) ? n : 0;

    public void Record(Guid jobId) => _counts.AddOrUpdate(jobId, 1, (_, n) => n + 1);

    public void Clear(Guid jobId) => _counts.TryRemove(jobId, out _);
}
