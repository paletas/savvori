using Savvori.Shared;

namespace Savvori.WebApi.Scraping;

/// <summary>Live-progress view of a running job, computed on read (nothing here is stored).</summary>
public sealed record ScrapeProgressView(
    int? ProductsSeen,
    string? Key,
    int? Offset,
    int? Total,
    bool TotalIsEstimate,
    int? KeysDone,
    int? KeysTotal,
    DateTime? LastProgressAt,
    DateTime? LastHeartbeatAt,
    double? EtaSeconds,
    bool EtaIsEstimate,
    bool Stalled);

/// <summary>Pure progress math: total basis, ETA and the keep-alive based stalled flag.</summary>
public static class ScrapeProgressEstimator
{
    public static readonly TimeSpan DefaultStalledAfter = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Stalled means the job is Running and its heartbeat (StartedAt before the first beat) is older than
    /// <paramref name="stalledAfter"/>. Page progress is deliberately not considered.
    /// </summary>
    public static bool IsStalled(ScrapingJob job, DateTime now, TimeSpan stalledAfter)
        => job.Status == ScrapingJobStatus.Running &&
           now - (job.LastHeartbeatAt ?? job.StartedAt) > stalledAfter;

    /// <summary>Returns null unless the job is Running.</summary>
    public static ScrapeProgressView? Build(
        ScrapingJob job, int? previousHealthyCount, DateTime now, TimeSpan stalledAfter)
    {
        if (job.Status != ScrapingJobStatus.Running) return null;

        var seen = job.ProgressProductsSeen;
        // Declared total (single-key run) wins; otherwise the last healthy run's count is an estimate.
        int? total = job.ProgressTotal is > 0 ? job.ProgressTotal
            : previousHealthyCount is > 0 ? previousHealthyCount : null;
        var declared = job.ProgressTotal is > 0;

        double? eta = null;
        var etaIsEstimate = !declared;
        var elapsed = job.LastProgressAt is { } lp ? (lp - job.StartedAt).TotalSeconds : 0;
        if (seen is > 0 && elapsed > 0 && total is { } t)
        {
            var remaining = Math.Max(0, t - seen.Value);
            eta = remaining / (seen.Value / elapsed);
        }
        else if (total is null && elapsed > 0 &&
                 job.ProgressKeysDone is >= 1 && job.ProgressKeysTotal is { } kt && kt > job.ProgressKeysDone)
        {
            eta = elapsed * (kt - job.ProgressKeysDone.Value) / job.ProgressKeysDone.Value;
            etaIsEstimate = true;
        }

        return new ScrapeProgressView(
            seen, job.ProgressKey, job.ProgressOffset, total, TotalIsEstimate: !declared,
            job.ProgressKeysDone, job.ProgressKeysTotal, job.LastProgressAt, job.LastHeartbeatAt,
            eta, etaIsEstimate, IsStalled(job, now, stalledAfter));
    }

    /// <summary>Last healthy run's product count for the chain, from jobs already loaded.</summary>
    public static int? PreviousHealthyCount(IEnumerable<ScrapingJob> chainJobs, Guid excludeJobId)
        => chainJobs
            .Where(j => j.Id != excludeJobId &&
                        (j.Status == ScrapingJobStatus.Completed || j.Status == ScrapingJobStatus.CompletedWithWarnings))
            .OrderByDescending(j => j.StartedAt)
            .Select(j => (int?)j.ProductsScraped)
            .FirstOrDefault();
}
