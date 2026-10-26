using System.Diagnostics;
using System.Diagnostics.Metrics;
using Savvori.Shared;

namespace Savvori.WebApi.Scraping;

/// <summary>
/// Centralised OpenTelemetry instrumentation for scraping jobs.
/// Register as a singleton so ActivitySource and Meter are shared across scopes.
/// </summary>
public sealed class ScrapingTelemetry : IDisposable
{
    public const string ActivitySourceName = "Savvori.Scraping";
    public const string MeterName = "Savvori.Scraping";

    private readonly ActivitySource _source = new(ActivitySourceName, "1.0.0");
    private readonly Meter _meter = new(MeterName, "1.0.0");

    // ---- Counters -------------------------------------------------------

    /// <summary>Total scraping jobs started (all chains).</summary>
    public readonly Counter<long> JobsStarted;

    /// <summary>Scraping jobs that completed successfully.</summary>
    public readonly Counter<long> JobsCompleted;

    /// <summary>Scraping jobs that threw an unhandled exception.</summary>
    public readonly Counter<long> JobsFailed;

    /// <summary>Products persisted to the database across all completed jobs.</summary>
    public readonly Counter<long> ProductsScraped;

    /// <summary>Store locations persisted to the database across all completed jobs.</summary>
    public readonly Counter<long> LocationsScraped;

    /// <summary>Products whose category assignment failed and stayed uncategorised.</summary>
    public readonly Counter<long> CategoriesFailed;

    /// <summary>Stale-price marking runs skipped because the scrape health was not trustworthy.</summary>
    public readonly Counter<long> StaleMarkingSkipped;

    /// <summary>Products deactivated because their prices were judged stale.</summary>
    public readonly Counter<long> ProductsDeactivated;

    /// <summary>HTTP responses received by the scrapers.</summary>
    public readonly Counter<long> HttpResponses;

    /// <summary>Discovery runs that fell back to the static category list.</summary>
    public readonly Counter<long> DiscoveryFallback;

    /// <summary>Canary checks that failed.</summary>
    public readonly Counter<long> CanaryFailed;

    /// <summary>URLs reported as blocked.</summary>
    public readonly Counter<long> Blocked;

    // ---- Histograms -----------------------------------------------------

    /// <summary>Total wall-clock duration of a scraping job in milliseconds.</summary>
    public readonly Histogram<double> JobDurationMs;

    // ---- Ctor -----------------------------------------------------------

    public ScrapingTelemetry()
    {
        JobsStarted = _meter.CreateCounter<long>(
            "savvori.scraping.jobs.started", "jobs",
            "Number of scraping jobs started");

        JobsCompleted = _meter.CreateCounter<long>(
            "savvori.scraping.jobs.completed", "jobs",
            "Number of scraping jobs completed successfully");

        JobsFailed = _meter.CreateCounter<long>(
            "savvori.scraping.jobs.failed", "jobs",
            "Number of scraping jobs that failed with an unhandled exception");

        ProductsScraped = _meter.CreateCounter<long>(
            "savvori.scraping.products.scraped", "products",
            "Number of products scraped and persisted to the database");

        LocationsScraped = _meter.CreateCounter<long>(
            "savvori.scraping.locations.scraped", "locations",
            "Number of store locations scraped and persisted to the database");

        CategoriesFailed = _meter.CreateCounter<long>(
            "savvori.scraping.categories.failed", "categories",
            "Number of products whose category assignment failed");

        StaleMarkingSkipped = _meter.CreateCounter<long>(
            "savvori.scraping.stale_marking.skipped", "runs",
            "Number of stale-price marking runs skipped due to untrusted scrape health");

        ProductsDeactivated = _meter.CreateCounter<long>(
            "savvori.scraping.products.deactivated", "products",
            "Number of products deactivated as stale");

        HttpResponses = _meter.CreateCounter<long>(
            "savvori.scraping.http.responses", "responses",
            "Number of HTTP responses received by scrapers");

        DiscoveryFallback = _meter.CreateCounter<long>(
            "savvori.scraping.discovery.fallback", "runs",
            "Number of discovery runs that fell back to the static category list");

        CanaryFailed = _meter.CreateCounter<long>(
            "savvori.scraping.canary.failed", "runs",
            "Number of canary checks that failed");

        Blocked = _meter.CreateCounter<long>(
            "savvori.scraping.blocked", "urls",
            "Number of URLs reported as blocked");

        JobDurationMs = _meter.CreateHistogram<double>(
            "savvori.scraping.job.duration", "ms",
            "Wall-clock duration of a complete scraping job");
    }

    // ---- Activity helpers -----------------------------------------------

    /// <summary>Starts the root span for an entire scraping job.</summary>
    public Activity? StartJobActivity(string chainSlug) =>
        _source.StartActivity($"scrape {chainSlug}", ActivityKind.Internal)
               ?.SetTag("job.system", "quartz")
               ?.SetTag("store.chain.slug", chainSlug);

    /// <summary>Starts a child span for one phase (locations or products) of a job.</summary>
    public Activity? StartPhaseActivity(string phase, string chainSlug) =>
        _source.StartActivity($"scrape {chainSlug} {phase}", ActivityKind.Internal)
               ?.SetTag("store.chain.slug", chainSlug)
               ?.SetTag("scraping.phase", phase);

    /// <summary>The shared <see cref="Meter"/> that owns the scraping counters, for external instruments.</summary>
    public Meter Meter => _meter;

    /// <summary>Emits the per-run health counters for one finished scrape run.</summary>
    public void RecordRun(string slug, ScrapeRunResult result, RunHealth health, ProcessResult processed)
    {
        var tags = new TagList { { "store.chain.slug", slug } };

        var failed = result.Outcomes.Count(o => o.Result is not (OutcomeResult.Ok or OutcomeResult.Recovered));
        if (failed > 0) CategoriesFailed.Add(failed, tags);

        var blocked = result.Outcomes.Count(o => o.Result == OutcomeResult.Blocked);
        if (blocked > 0) Blocked.Add(blocked, tags);

        var byStatus = new Dictionary<int, int>();
        foreach (var o in result.Outcomes)
            foreach (var (code, n) in o.StatusCounts)
                byStatus[code] = byStatus.GetValueOrDefault(code) + n;
        foreach (var (code, n) in byStatus)
            HttpResponses.Add(n, new TagList { { "store.chain.slug", slug }, { "http.status", code.ToString() } });

        if (result.DiscoveryFallbackUsed) DiscoveryFallback.Add(1, tags);
        if (result.Canary is { Ok: false }) CanaryFailed.Add(1, tags);
        if (health.Status == ScrapingJobStatus.Degraded) StaleMarkingSkipped.Add(1, tags);
        if (processed.StaleMarked > 0) ProductsDeactivated.Add(processed.StaleMarked, tags);
    }

    public void Dispose()
    {
        _source.Dispose();
        _meter.Dispose();
    }
}
