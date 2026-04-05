using System.Diagnostics;
using System.Diagnostics.Metrics;

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

    public void Dispose()
    {
        _source.Dispose();
        _meter.Dispose();
    }
}
