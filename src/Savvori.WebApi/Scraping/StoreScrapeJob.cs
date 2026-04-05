using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Quartz;
using Savvori.Shared;

namespace Savvori.WebApi.Scraping;

/// <summary>
/// Quartz job that runs a specific store scraper identified by <see cref="StoreChainSlugKey"/> in
/// the job data map. Creates a <see cref="ScrapingJob"/> record to track progress, and emits
/// OpenTelemetry traces and metrics via <see cref="ScrapingTelemetry"/>.
/// </summary>
[DisallowConcurrentExecution]
public sealed class StoreScrapeJob : IJob
{
    public const string StoreChainSlugKey = "StoreChainSlug";
    public const string ScrapeLocationsKey = "ScrapeLocations";

    private readonly IEnumerable<IStoreScraper> _scrapers;
    private readonly ScraperResultProcessor _processor;
    private readonly SavvoriDbContext _db;
    private readonly ScrapingTelemetry _telemetry;
    private readonly ILogger<StoreScrapeJob> _logger;

    public StoreScrapeJob(
        IEnumerable<IStoreScraper> scrapers,
        ScraperResultProcessor processor,
        SavvoriDbContext db,
        ScrapingTelemetry telemetry,
        ILogger<StoreScrapeJob> logger)
    {
        _scrapers = scrapers;
        _processor = processor;
        _db = db;
        _telemetry = telemetry;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var slug = context.MergedJobDataMap.GetString(StoreChainSlugKey);
        if (string.IsNullOrEmpty(slug))
        {
            _logger.LogError("StoreScrapeJob fired without a '{Key}' key in job data.", StoreChainSlugKey);
            return;
        }

        var scraper = _scrapers.FirstOrDefault(s => s.StoreChainSlug == slug);
        if (scraper is null)
        {
            _logger.LogError("No IStoreScraper registered for slug '{Slug}'.", slug);
            return;
        }

        var chain = _db.StoreChains.FirstOrDefault(sc => sc.Slug == slug);
        if (chain is null)
        {
            _logger.LogError("StoreChain '{Slug}' not found in database.", slug);
            return;
        }

        // Root span for the entire job
        using var jobActivity = _telemetry.StartJobActivity(slug);
        var sw = Stopwatch.StartNew();
        var tags = new TagList { { "store.chain.slug", slug } };
        _telemetry.JobsStarted.Add(1, tags);

        var job = new ScrapingJob
        {
            Id = Guid.NewGuid(),
            StoreChainId = chain.Id,
            Status = ScrapingJobStatus.Running,
            StartedAt = DateTime.UtcNow
        };
        _db.ScrapingJobs.Add(job);
        await _db.SaveChangesAsync(context.CancellationToken);

        var scrapeLocations = context.MergedJobDataMap.GetBoolean(ScrapeLocationsKey);

        try
        {
            var locationCount = 0;

            if (scrapeLocations)
            {
                using var locActivity = _telemetry.StartPhaseActivity("locations", slug);
                try
                {
                    _logger.LogInformation("Scraping store locations for {Slug}", slug);
                    var locations = await scraper.ScrapeStoreLocationsAsync(context.CancellationToken);
                    await _processor.ProcessStoreLocationsAsync(slug, locations, context.CancellationToken);
                    locationCount = locations.Count;

                    locActivity?.SetTag("scraping.result.count", locationCount);
                    locActivity?.SetStatus(ActivityStatusCode.Ok);

                    await AddLogAsync(job, ScrapingLogLevel.Info,
                        $"Scraped {locations.Count} store locations.");
                }
                catch (Exception ex)
                {
                    RecordException(locActivity, ex);
                    locActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    // Non-fatal: log and continue to product scraping
                    _logger.LogWarning(ex, "Location scraping failed for {Slug}; continuing with products", slug);
                    await AddLogAsync(job, ScrapingLogLevel.Warning,
                        $"Location scraping failed: {ex.Message}");
                }
            }

            // Product scraping phase
            using var prodActivity = _telemetry.StartPhaseActivity("products", slug);
            _logger.LogInformation("Scraping products for {Slug}", slug);
            var products = await scraper.ScrapeProductsAsync(ct: context.CancellationToken);
            var count = await _processor.ProcessProductsAsync(slug, products, context.CancellationToken);

            prodActivity?.SetTag("scraping.result.count", count);
            prodActivity?.SetStatus(ActivityStatusCode.Ok);

            sw.Stop();
            await MarkCompletedAsync(job.Id, count);

            jobActivity?.SetTag("scraping.products.count", count);
            jobActivity?.SetTag("scraping.locations.count", locationCount);
            jobActivity?.SetStatus(ActivityStatusCode.Ok);

            _telemetry.JobsCompleted.Add(1, tags);
            _telemetry.JobDurationMs.Record(sw.Elapsed.TotalMilliseconds, tags);
            if (count > 0) _telemetry.ProductsScraped.Add(count, tags);
            if (locationCount > 0) _telemetry.LocationsScraped.Add(locationCount, tags);

            await AddLogAsync(job, ScrapingLogLevel.Info,
                $"Completed. Scraped {count} products.");
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Scraping job failed for {Slug}", slug);
            await MarkFailedAsync(job.Id, ex.Message);

            jobActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            RecordException(jobActivity, ex);

            _telemetry.JobsFailed.Add(1, tags);
            _telemetry.JobDurationMs.Record(sw.Elapsed.TotalMilliseconds, tags);

            await AddLogAsync(job, ScrapingLogLevel.Error,
                $"Job failed: {ex.Message}");
        }
        finally
        {
            await _db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private async Task AddLogAsync(ScrapingJob job, ScrapingLogLevel level, string message)
    {
        _db.ScrapingLogs.Add(new ScrapingLog
        {
            Id = Guid.NewGuid(),
            ScrapingJobId = job.Id,
            Level = level,
            Message = message,
            Timestamp = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task MarkCompletedAsync(Guid jobId, int productsScraped)
    {
        var persistedJob = await _db.ScrapingJobs
            .FirstOrDefaultAsync(j => j.Id == jobId, CancellationToken.None);

        if (persistedJob is null)
            return;

        persistedJob.ProductsScraped = productsScraped;
        persistedJob.Status = ScrapingJobStatus.Completed;
        persistedJob.CompletedAt = DateTime.UtcNow;
        persistedJob.ErrorMessage = null;

        await _db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task MarkFailedAsync(Guid jobId, string errorMessage)
    {
        var persistedJob = await _db.ScrapingJobs
            .FirstOrDefaultAsync(j => j.Id == jobId, CancellationToken.None);

        if (persistedJob is null)
            return;

        persistedJob.Status = ScrapingJobStatus.Failed;
        persistedJob.CompletedAt = DateTime.UtcNow;
        persistedJob.ErrorMessage = errorMessage;

        await _db.SaveChangesAsync(CancellationToken.None);
    }

    private static void RecordException(Activity? activity, Exception ex)
    {
        if (activity is null) return;
        activity.AddEvent(new ActivityEvent("exception",
            tags: new ActivityTagsCollection
            {
                { "exception.type",       ex.GetType().FullName ?? "Exception" },
                { "exception.message",    ex.Message },
                { "exception.stacktrace", ex.StackTrace ?? string.Empty }
            }));
    }
}

