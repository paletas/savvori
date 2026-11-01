using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Quartz;
using Savvori.WebApi.Scraping;

namespace Savvori.WebApi.Controllers;

/// <summary>
/// Admin API for monitoring and triggering scraping jobs.
/// </summary>
[ApiController]
[Route("api/admin/scraping")]
public class ScrapingAdminController : ControllerBase
{
    private readonly SavvoriDbContext _db;
    private readonly ISchedulerFactory _schedulerFactory;
    private readonly IConfiguration _config;

    public ScrapingAdminController(SavvoriDbContext db, ISchedulerFactory schedulerFactory, IConfiguration config)
    {
        _db = db;
        _config = config;
        _schedulerFactory = schedulerFactory;
    }

    /// <summary>
    /// Get status of all scraping jobs (last run, products scraped, errors, next scheduled run).
    /// GET /api/admin/scraping/status
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken ct = default)
    {
        // All configured chains from DB — only active ones
        var chains = await _db.StoreChains
            .Where(c => c.IsActive)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

        // Latest job per chain
        var latestJobs = await _db.ScrapingJobs
            .Include(j => j.StoreChain)
            .OrderByDescending(j => j.StartedAt)
            .ToListAsync(ct);

        var latestByChain = latestJobs
            .GroupBy(j => j.StoreChainId)
            .ToDictionary(g => g.Key, g => g.First());

        // Keys of non-ok outcomes of each chain's latest job
        var latestIds = latestByChain.Values.Select(j => j.Id).ToList();
        var failedKeyRows = await _db.ScrapingRunOutcomes
            .Where(o => latestIds.Contains(o.ScrapingJobId) &&
                        !Savvori.Shared.OutcomeResults.NonFailures.Contains(o.Result))
            .OrderBy(o => o.Kind).ThenBy(o => o.Key)
            .Select(o => new { o.ScrapingJobId, o.Key })
            .ToListAsync(ct);
        var failedKeys = failedKeyRows
            .GroupBy(o => o.ScrapingJobId)
            .ToDictionary(g => g.Key, g => g.Select(o => o.Key).ToArray());

        // Next fire times from Quartz
        var scheduler = await _schedulerFactory.GetScheduler(ct);
        var nextFireTimes = new Dictionary<string, DateTime?>();
        foreach (var chain in chains)
        {
            var jobKey = new JobKey($"scrape-{chain.Slug}");
            if (await scheduler.Exists(jobKey, ct))
            {
                var triggers = await scheduler.GetTriggersOfJob(jobKey, ct);
                var nextTimes = triggers
                    .Select(t => t.NextFireTimeUtc)
                    .Where(t => t.HasValue)
                    .Select(t => t!.Value.UtcDateTime);
                nextFireTimes[chain.Slug] = nextTimes.Any() ? nextTimes.Min() : null;
            }
            else
            {
                nextFireTimes[chain.Slug] = null;
            }
        }

        var result = chains.Select(chain =>
        {
            latestByChain.TryGetValue(chain.Id, out var job);
            nextFireTimes.TryGetValue(chain.Slug, out var nextFire);

            return new
            {
                ChainSlug    = chain.Slug,
                ChainName    = chain.Name,
                IsScheduled  = nextFireTimes.ContainsKey(chain.Slug),
                NextFireTime = nextFire,
                LastStatus   = job?.Status,
                LastRunAt    = job?.StartedAt,
                CompletedAt  = job?.CompletedAt,
                ProductsScraped = job?.ProductsScraped ?? 0,
                ErrorMessage = job?.ErrorMessage,
                HealthReason = job?.HealthReason,
                WarningCount = job?.WarningCount ?? 0,
                StaleMarkingSkipped = job?.StaleMarkingSkipped ?? false,
                StaleMarked = job?.StaleMarked ?? 0,
                DropRatio = job?.DropRatio,
                FailedKeys = job is not null && failedKeys.TryGetValue(job.Id, out var keys) ? keys : Array.Empty<string>()
            };
        }).ToList();

        return Ok(result);
    }

    /// <summary>
    /// Get scraping job history for a specific store chain.
    /// GET /api/admin/scraping/status/{chainSlug}
    /// </summary>
    [HttpGet("status/{chainSlug}")]
    public async Task<IActionResult> GetChainStatus(string chainSlug, CancellationToken ct = default)
    {
        var chain = await _db.StoreChains
            .FirstOrDefaultAsync(sc => sc.Slug == chainSlug.ToLower(), ct);

        if (chain is null) return NotFound();

        var jobs = await _db.ScrapingJobs
            .Where(j => j.StoreChainId == chain.Id)
            .OrderByDescending(j => j.StartedAt)
            .Take(10)
            .Select(j => new
            {
                j.Id,
                j.Status,
                j.StartedAt,
                j.CompletedAt,
                j.ProductsScraped,
                j.ErrorMessage,
                j.HealthReason,
                j.WarningCount,
                j.StaleMarkingSkipped,
                j.DropRatio
            })
            .ToListAsync(ct);

        var outcomes = new List<object>();
        if (jobs.Count > 0)
        {
            var latestId = jobs[0].Id;
            var rows = await _db.ScrapingRunOutcomes
                .Where(o => o.ScrapingJobId == latestId)
                .OrderBy(o => o.Kind).ThenBy(o => o.Key)
                .Select(o => new
                {
                    Kind = o.Kind.ToString(),
                    o.Key,
                    o.Url,
                    Result = o.Result.ToString(),
                    o.PagesFetched,
                    o.ProductsFound,
                    o.TilesSeen,
                    o.Retries,
                    o.StatusCounts,
                    o.FirstError,
                    o.StopReason
                })
                .ToListAsync(ct);
            outcomes.AddRange(rows);
        }

        var logs = jobs.Select(j => j.Id).ToList();
        var recentLogs = await _db.ScrapingLogs
            .Where(l => logs.Contains(l.ScrapingJobId))
            .OrderByDescending(l => l.Timestamp)
            .Take(50)
            .Select(l => new
            {
                l.ScrapingJobId,
                l.Level,
                l.Message,
                l.Timestamp
            })
            .ToListAsync(ct);

        return Ok(new { Chain = chainSlug, Jobs = jobs, RecentLogs = recentLogs, Outcomes = outcomes });
    }

    /// <summary>
    /// Manually trigger a scraping job for a specific store chain.
    /// POST /api/admin/scraping/trigger/{chainSlug}
    /// </summary>
    [HttpPost("trigger/{chainSlug}")]
    public async Task<IActionResult> TriggerScrape(string chainSlug, CancellationToken ct = default)
    {
        var chain = await _db.StoreChains
            .FirstOrDefaultAsync(sc => sc.Slug == chainSlug.ToLower(), ct);

        if (chain is null) return NotFound($"Store chain '{chainSlug}' not found");

        try
        {
            var scheduler = await _schedulerFactory.GetScheduler(ct);
            var jobKey = new JobKey($"scrape-{chainSlug}");

            if (!await scheduler.Exists(jobKey, ct))
                return BadRequest($"No scheduled job found for '{chainSlug}'. Ensure it is enabled in Scraping:Chains config.");

            var dataMap = new JobDataMap
            {
                { StoreScrapeJob.StoreChainSlugKey, chainSlug },
                { StoreScrapeJob.ScrapeLocationsKey, true }
            };

            await scheduler.TriggerJob(jobKey, dataMap, ct);
            return Accepted(new { Message = $"Scraping job triggered for '{chainSlug}'", ChainSlug = chainSlug });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.Message });
        }
    }

    /// <summary>
    /// Reactivate inactive products last scraped within each chain's grace window (backfill after the
    /// health-gated stale rule). Dry run unless apply=true.
    /// POST /api/admin/scraping/reactivate-recent?apply=true
    /// </summary>
    [HttpPost("reactivate-recent")]
    public async Task<IActionResult> ReactivateRecent([FromQuery] bool apply = false, CancellationToken ct = default)
    {
        var configured = _config.GetSection("Scraping:Chains").Get<List<ScrapingChainConfig>>() ?? [];
        var chains = await _db.StoreChains.Where(c => c.IsActive).OrderBy(c => c.Name).ToListAsync(ct);

        var results = new List<object>();
        foreach (var chain in chains)
        {
            var graceDays = configured.FirstOrDefault(c => c.Slug == chain.Slug)?.EffectiveGraceDays
                ?? new ScrapingChainConfig { Slug = chain.Slug }.EffectiveGraceDays;
            var cutoff = DateTime.UtcNow.AddDays(-graceDays);
            var products = await _db.StoreProducts
                .Where(sp => sp.StoreChainId == chain.Id && !sp.IsActive && sp.LastScraped >= cutoff)
                .ToListAsync(ct);

            if (apply)
            {
                foreach (var sp in products) sp.IsActive = true;
                await _db.SaveChangesAsync(ct);
            }

            results.Add(new { ChainSlug = chain.Slug, GraceDays = graceDays, Count = products.Count, Applied = apply });
        }
        return Ok(results);
    }
}
