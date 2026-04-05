using Microsoft.AspNetCore.Authorization;
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
[Authorize(Policy = "AdminOnly")]
public class ScrapingAdminController : ControllerBase
{
    private readonly SavvoriDbContext _db;
    private readonly ISchedulerFactory _schedulerFactory;

    public ScrapingAdminController(SavvoriDbContext db, ISchedulerFactory schedulerFactory)
    {
        _db = db;
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

        // Next fire times from Quartz
        var scheduler = await _schedulerFactory.GetScheduler(ct);
        var nextFireTimes = new Dictionary<string, DateTime?>();
        foreach (var chain in chains)
        {
            var jobKey = new JobKey($"scrape-{chain.Slug}");
            if (await scheduler.CheckExists(jobKey, ct))
            {
                var triggers = await scheduler.GetTriggersOfJob(jobKey, ct);
                var nextTimes = triggers
                    .Select(t => t.GetNextFireTimeUtc())
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
                ErrorMessage = job?.ErrorMessage
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
                j.ErrorMessage
            })
            .ToListAsync(ct);

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

        return Ok(new { Chain = chainSlug, Jobs = jobs, RecentLogs = recentLogs });
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

            if (!await scheduler.CheckExists(jobKey, ct))
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
}
