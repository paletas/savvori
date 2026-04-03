using Quartz;
using Savvori.Shared;

namespace Savvori.WebApi.Scraping;

/// <summary>
/// Quartz job that runs a specific store scraper identified by <see cref="StoreChainSlugKey"/> in
/// the job data map. Creates a <see cref="ScrapingJob"/> record to track progress.
/// </summary>
[DisallowConcurrentExecution]
public sealed class StoreScrapeJob : IJob
{
    public const string StoreChainSlugKey = "StoreChainSlug";
    public const string ScrapeLocationsKey = "ScrapeLocations";

    private readonly IEnumerable<IStoreScraper> _scrapers;
    private readonly ScraperResultProcessor _processor;
    private readonly SavvoriDbContext _db;
    private readonly ILogger<StoreScrapeJob> _logger;

    public StoreScrapeJob(
        IEnumerable<IStoreScraper> scrapers,
        ScraperResultProcessor processor,
        SavvoriDbContext db,
        ILogger<StoreScrapeJob> logger)
    {
        _scrapers = scrapers;
        _processor = processor;
        _db = db;
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
            // Optionally refresh store locations first
            if (scrapeLocations)
            {
                _logger.LogInformation("Scraping store locations for {Slug}", slug);
                var locations = await scraper.ScrapeStoreLocationsAsync(context.CancellationToken);
                await _processor.ProcessStoreLocationsAsync(slug, locations, context.CancellationToken);
                await AddLogAsync(job, ScrapingLogLevel.Info,
                    $"Scraped {locations.Count} store locations.", context.CancellationToken);
            }

            // Scrape products
            _logger.LogInformation("Scraping products for {Slug}", slug);
            var products = await scraper.ScrapeProductsAsync(ct: context.CancellationToken);
            var count = await _processor.ProcessProductsAsync(slug, products, context.CancellationToken);

            job.ProductsScraped = count;
            job.Status = ScrapingJobStatus.Completed;
            job.CompletedAt = DateTime.UtcNow;
            await AddLogAsync(job, ScrapingLogLevel.Info,
                $"Completed. Scraped {count} products.", context.CancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scraping job failed for {Slug}", slug);
            job.Status = ScrapingJobStatus.Failed;
            job.CompletedAt = DateTime.UtcNow;
            job.ErrorMessage = ex.Message;
            await AddLogAsync(job, ScrapingLogLevel.Error,
                $"Job failed: {ex.Message}", context.CancellationToken);
        }
        finally
        {
            await _db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private async Task AddLogAsync(ScrapingJob job, ScrapingLogLevel level, string message, CancellationToken ct)
    {
        _db.ScrapingLogs.Add(new ScrapingLog
        {
            Id = Guid.NewGuid(),
            ScrapingJobId = job.Id,
            Level = level,
            Message = message,
            Timestamp = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(ct);
    }
}
