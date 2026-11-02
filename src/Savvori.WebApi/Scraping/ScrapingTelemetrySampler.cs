using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Savvori.Shared;

namespace Savvori.WebApi.Scraping;

/// <summary>
/// Samples the last scrape runs of every active store chain every 60s and publishes them through four observable
/// gauges. The database read happens in this loop, not in a gauge callback (callbacks must stay synchronous and
/// cheap): the callbacks only read the immutable snapshot the loop last published, one measurement per chain.
/// </summary>
public sealed class ScrapingTelemetrySampler : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ScrapingTelemetrySampler> _logger;

    /// <summary>One active chain's picture, taken from its last 20 finished runs. Immutable, one per snapshot.</summary>
    public sealed record Sample(
        string Slug, int Health, double? DropRatio, double AgeHours, int StaleSkippedConsecutive);

    /// <summary>The loop writes a whole new list; the gauge callbacks read whatever is current. Never mutated in place.</summary>
    private volatile IReadOnlyList<Sample> _samples = [];

    public ScrapingTelemetrySampler(
        IServiceScopeFactory scopes, ScrapingTelemetry telemetry, ILogger<ScrapingTelemetrySampler> logger)
    {
        _scopes = scopes;
        _logger = logger;

        // The instruments live on ScrapingTelemetry's Meter (the singleton owns it); nothing here needs to hold them.
        telemetry.Meter.CreateObservableGauge<double>(
            "savvori.scraping.run.health",
            () => Measure(s => (double?)s.Health),
            "1",
            "Health of the latest finished scrape run per chain: 0 Completed, 1 CompletedWithWarnings, 2 Degraded, 3 Failed.");

        telemetry.Meter.CreateObservableGauge<double>(
            "savvori.scraping.drop_ratio",
            () => Measure(s => s.DropRatio),
            "ratio",
            "Product-count drop ratio of the latest finished scrape run per chain; a chain whose run recorded none is absent.");

        telemetry.Meter.CreateObservableGauge<double>(
            "savvori.scraping.last_run_age_hours",
            () => Measure(s => s.AgeHours),
            "h",
            "Hours since the latest finished scrape run per chain (CompletedAt, falling back to StartedAt).");

        telemetry.Meter.CreateObservableGauge<double>(
            "savvori.scraping.stale_skipped.consecutive",
            () => Measure(s => (double?)s.StaleSkippedConsecutive),
            "runs",
            "Finished runs, newest first, that skipped stale-price marking in a row for that chain.");
    }

    /// <summary>Read the last 20 finished runs of every active chain and publish the new snapshot.</summary>
    public async Task SampleAsync(CancellationToken ct)
    {
        var samples = new List<Sample>();

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SavvoriDbContext>();

        var chains = await db.StoreChains.AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.Slug)
            .Select(c => new { c.Id, c.Slug })
            .ToListAsync(ct);

        foreach (var chain in chains)
        {
            var jobs = await db.ScrapingJobs.AsNoTracking()
                .Where(j => j.StoreChainId == chain.Id
                            && (j.Status == ScrapingJobStatus.Completed
                                || j.Status == ScrapingJobStatus.Failed
                                || j.Status == ScrapingJobStatus.CompletedWithWarnings
                                || j.Status == ScrapingJobStatus.Degraded))
                .OrderByDescending(j => j.StartedAt)
                .Take(20)
                .ToListAsync(ct);

            // A chain with no finished job is omitted rather than reported as unknown.
            var latest = jobs.FirstOrDefault();
            if (latest is null) continue;

            var health = latest.Status switch
            {
                ScrapingJobStatus.Completed => 0,
                ScrapingJobStatus.CompletedWithWarnings => 1,
                ScrapingJobStatus.Degraded => 2,
                _ => 3, // Failed - the only other status this query returns
            };

            samples.Add(new Sample(
                chain.Slug,
                health,
                latest.DropRatio,
                (DateTime.UtcNow - (latest.CompletedAt ?? latest.StartedAt)).TotalHours,
                jobs.TakeWhile(j => j.StaleMarkingSkipped).Count()));
        }

        _samples = samples.Count == 0 ? [] : samples.AsReadOnly();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SampleAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Scraping telemetry sample failed; leaving the previous values in place.");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>One measurement per chain in the current snapshot; a null value publishes nothing for that chain.</summary>
    private IEnumerable<Measurement<double>> Measure(Func<Sample, double?> selector)
    {
        foreach (var s in _samples)
        {
            var value = selector(s);
            if (value is null) continue;
            yield return new Measurement<double>(
                value.Value, new KeyValuePair<string, object?>("store.chain.slug", s.Slug));
        }
    }
}
