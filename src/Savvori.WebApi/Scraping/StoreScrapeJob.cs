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
    private readonly IConfiguration? _config;

    public StoreScrapeJob(
        IEnumerable<IStoreScraper> scrapers,
        ScraperResultProcessor processor,
        SavvoriDbContext db,
        ScrapingTelemetry telemetry,
        ILogger<StoreScrapeJob> logger,
        IConfiguration? config = null)
    {
        _config = config;
        _scrapers = scrapers;
        _processor = processor;
        _db = db;
        _telemetry = telemetry;
        _logger = logger;
    }

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
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
            var canary = await scraper.ProbeAsync(context.CancellationToken);
            ScrapeRunResult result;
            if (!canary.Ok)
            {
                _logger.LogWarning("Canary failed for {Slug}: {Message}", slug, canary.Message);
                result = new ScrapeRunResult([], [], [], Canary: canary);
            }
            else
            {
                result = (await scraper.ScrapeProductsAsync(ct: context.CancellationToken)) with { Canary = canary };
            }
            var products = result.Products;
            var health = ScrapeHealthEvaluator.Evaluate(result);
            var graceDays = _config?.GetSection("Scraping:Chains").Get<List<ScrapingChainConfig>>()
                ?.FirstOrDefault(c => c.Slug == slug)?.EffectiveGraceDays
                ?? new ScrapingChainConfig { Slug = slug }.EffectiveGraceDays;
            var since = DateTime.UtcNow.AddDays(-60);
            var starts = await _db.ScrapingJobs
                .Where(j => j.StoreChainId == chain.Id && j.Id != job.Id &&
                            (j.Status == ScrapingJobStatus.Completed || j.Status == ScrapingJobStatus.CompletedWithWarnings) &&
                            j.StartedAt >= since)
                .Select(j => j.StartedAt)
                .ToListAsync(CancellationToken.None);
            if (health.Status != ScrapingJobStatus.Degraded) starts.Add(job.StartedAt);
            var policy = new StalePolicy(health.Status != ScrapingJobStatus.Degraded, TimeSpan.FromDays(graceDays), starts);
            var processResult = await _processor.ProcessProductsAsync(slug, products, policy, context.CancellationToken);
            var count = processResult.Processed;

            prodActivity?.SetTag("scraping.result.count", count);
            prodActivity?.SetStatus(ActivityStatusCode.Ok);

            sw.Stop();
            await MarkCompletedAsync(job.Id, chain.Id, count, products.Count, health, processResult);
            var persisted = canary.Ok ? result : result with
            {
                Outcomes = [new ScrapeOutcome { Kind = OutcomeKind.Canary, Key = "canary", Url = slug, Result = OutcomeResult.Failed, FirstError = canary.Message }]
            };
            await PersistOutcomesAsync(job, persisted, health);
            await PruneOutcomesAsync();
            _telemetry.RecordRun(slug, persisted, health, processResult);

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

    private async Task MarkCompletedAsync(
        Guid jobId, Guid chainId, int productsScraped, int productsReturned, RunHealth health, ProcessResult processResult)
    {
        var persistedJob = await _db.ScrapingJobs
            .FirstOrDefaultAsync(j => j.Id == jobId, CancellationToken.None);

        if (persistedJob is null)
            return;

        var previous = await _db.ScrapingJobs
            .Where(j => j.StoreChainId == chainId && j.Id != jobId && j.StartedAt < persistedJob.StartedAt &&
                        (j.Status == ScrapingJobStatus.Completed || j.Status == ScrapingJobStatus.CompletedWithWarnings))
            .OrderByDescending(j => j.StartedAt)
            .Select(j => (int?)j.ProductsScraped)
            .FirstOrDefaultAsync(CancellationToken.None);

        persistedJob.Status = health.Status;
        persistedJob.ProductsScraped = productsScraped;
        persistedJob.ProductsReturned = productsReturned;
        persistedJob.HealthReason = health.Reasons.Count > 0 ? string.Join("; ", health.Reasons) : null;
        persistedJob.WarningCount = health.Warnings.Count;
        persistedJob.StaleMarked = processResult.StaleMarked;
        persistedJob.StaleMarkingSkipped = health.Status == ScrapingJobStatus.Degraded;
        persistedJob.PreviousHealthyCount = previous;
        persistedJob.DropRatio = previous > 0 ? productsReturned / (double)previous : null;
        persistedJob.CompletedAt = DateTime.UtcNow;
        persistedJob.ErrorMessage = null;

        // Advisory only: never used for status.
        _logger.LogInformation("Scrape {JobId} returned {Returned} products, previous healthy {Previous}, drop ratio {DropRatio}.",
            jobId, productsReturned, previous, persistedJob.DropRatio);

        await _db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task PersistOutcomesAsync(ScrapingJob job, ScrapeRunResult result, RunHealth health)
    {
        static string Trunc(string s) => s.Length > 500 ? s[..500] : s;

        foreach (var o in result.Outcomes)
        {
            _db.ScrapingRunOutcomes.Add(new ScrapingRunOutcome
            {
                Id = Guid.NewGuid(),
                ScrapingJobId = job.Id,
                Kind = o.Kind,
                Key = o.Key,
                Url = Trunc(o.Url),
                Result = o.Result,
                PagesFetched = o.PagesFetched,
                ProductsFound = o.ProductsFound,
                TilesSeen = o.TilesSeen,
                Retries = o.Retries,
                StatusCounts = string.Join(",", o.StatusCounts.OrderBy(k => k.Key).Select(k => $"{k.Key}:{k.Value}")),
                FirstError = o.FirstError is null ? null : Trunc(o.FirstError)
            });
        }
        await _db.SaveChangesAsync(CancellationToken.None);

        foreach (var reason in health.Reasons)
            await AddLogAsync(job, ScrapingLogLevel.Error, reason);
        foreach (var warning in health.Warnings)
            await AddLogAsync(job, ScrapingLogLevel.Warning, warning);
    }

    private async Task PruneOutcomesAsync()
    {
        var cutoff = DateTime.UtcNow.AddDays(-90);
        var old = await _db.ScrapingRunOutcomes
            .Where(o => o.ScrapingJob.StartedAt < cutoff)
            .ToListAsync(CancellationToken.None);
        if (old.Count == 0) return;
        _db.ScrapingRunOutcomes.RemoveRange(old);
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

