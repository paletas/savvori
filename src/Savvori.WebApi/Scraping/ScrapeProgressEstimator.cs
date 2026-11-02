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

    /// <summary>
    /// Returns null unless the job is Running. <paramref name="previousKeyPages"/> is the last healthy run's
    /// PagesFetched per outcome key (null when there is none); it weights the keys still to do.
    /// Elapsed time is anchored to <paramref name="now"/>, not to the last progress write.
    /// </summary>
    public static ScrapeProgressView? Build(
        ScrapingJob job, int? previousHealthyCount, DateTime now, TimeSpan stalledAfter,
        IReadOnlyDictionary<string, int>? previousKeyPages = null)
    {
        if (job.Status != ScrapingJobStatus.Running) return null;

        var seen = job.ProgressProductsSeen;
        // Declared total (single-key run) wins; otherwise the last healthy run's count is an estimate.
        int? total = job.ProgressTotal is > 0 ? job.ProgressTotal
            : previousHealthyCount is > 0 ? previousHealthyCount : null;
        var declared = job.ProgressTotal is > 0;

        double? eta = null;
        var etaIsEstimate = !declared;
        var elapsed = Math.Max(0, (now - job.StartedAt).TotalSeconds);

        if (job.ProgressKeysTotal is > 1 && job.ProgressPagesDone is > 0 && elapsed > 0 &&
            EstimateRemainingPages(job, previousKeyPages) is { } pages)
        {
            // Page-weighted: measured seconds per page x estimated pages still to fetch.
            eta = elapsed / job.ProgressPagesDone.Value * pages;
            etaIsEstimate = true;
        }
        else if (declared && elapsed > 0)
        {
            // Single key with a site-declared total: offset against that total.
            var done = job.ProgressOffset is > 0 ? job.ProgressOffset.Value : seen ?? 0;
            if (done > 0) eta = elapsed * Math.Max(0, job.ProgressTotal!.Value - done) / done;
        }
        else if (seen is > 0 && elapsed > 0 && total is { } t)
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

    /// <summary>
    /// Pages still to fetch: remainder of the current key plus every key not yet done. Expected pages of a key
    /// come from the last healthy run's PagesFetched, else the key's declared counter (current key only), else the
    /// average pages of the keys finished so far. Null when nothing is known to weigh with.
    /// </summary>
    internal static double? EstimateRemainingPages(ScrapingJob job, IReadOnlyDictionary<string, int>? history)
    {
        var keysTotal = job.ProgressKeysTotal ?? 0;
        var doneKeys = ParseDoneKeys(job.ProgressDoneKeys);
        var doneCount = Math.Max(job.ProgressKeysDone ?? 0, doneKeys.Count);
        var key = job.ProgressKey;
        var currentOpen = key is not null && key != "processing" && !doneKeys.Contains(key);
        var keyPages = currentOpen ? job.ProgressKeyPages ?? 0 : 0;
        var finishedPages = Math.Max(0, (job.ProgressPagesDone ?? 0) - keyPages);
        double? avgDone = doneCount > 0 && finishedPages > 0 ? (double)finishedPages / doneCount : null;
        history ??= new Dictionary<string, int>();

        double remaining = 0;
        var unknown = false;

        if (currentOpen)
        {
            double? expected = history.TryGetValue(key!, out var h) ? h
                : job.ProgressKeyPagesTotal is > 0 ? job.ProgressKeyPagesTotal
                : avgDone;
            if (expected is { } e) remaining += Math.Max(0, e - keyPages);
            else unknown = true;
        }

        var keysLeft = Math.Max(0, keysTotal - doneCount - (currentOpen ? 1 : 0));
        var pending = history.Where(kv => kv.Key != key && !doneKeys.Contains(kv.Key)).Select(kv => (double)kv.Value).ToList();
        if (pending.Count > keysLeft && pending.Count > 0)
        {
            // The key set shrank since the last run: scale the known weights down to the keys that remain.
            remaining += pending.Sum() * keysLeft / pending.Count;
        }
        else
        {
            remaining += pending.Sum();
            var extra = keysLeft - pending.Count;
            if (extra > 0)
            {
                var avg = avgDone ?? (pending.Count > 0 ? pending.Average() : (double?)null);
                if (avg is { } a) remaining += extra * a;
                else unknown = true;
            }
        }

        return unknown ? null : remaining;
    }

    private static HashSet<string> ParseDoneKeys(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try { return (System.Text.Json.JsonSerializer.Deserialize<string[]>(json) ?? []).ToHashSet(); }
        catch (System.Text.Json.JsonException) { return []; }
    }

    /// <summary>Id of the last healthy run for the chain, from jobs already loaded.</summary>
    public static Guid? PreviousHealthyJobId(IEnumerable<ScrapingJob> chainJobs, Guid excludeJobId)
        => chainJobs
            .Where(j => j.Id != excludeJobId &&
                        (j.Status == ScrapingJobStatus.Completed || j.Status == ScrapingJobStatus.CompletedWithWarnings))
            .OrderByDescending(j => j.StartedAt)
            .Select(j => (Guid?)j.Id)
            .FirstOrDefault();

    /// <summary>Last healthy run's product count for the chain, from jobs already loaded.</summary>
    public static int? PreviousHealthyCount(IEnumerable<ScrapingJob> chainJobs, Guid excludeJobId)
        => chainJobs
            .Where(j => j.Id != excludeJobId &&
                        (j.Status == ScrapingJobStatus.Completed || j.Status == ScrapingJobStatus.CompletedWithWarnings))
            .OrderByDescending(j => j.StartedAt)
            .Select(j => (int?)j.ProductsScraped)
            .FirstOrDefault();
}
