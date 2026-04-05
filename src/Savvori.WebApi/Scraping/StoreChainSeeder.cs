using Microsoft.EntityFrameworkCore;
using Savvori.Shared;

namespace Savvori.WebApi.Scraping;

public static class StoreChainSeeder
{
    public static async Task SeedAsync(SavvoriDbContext db, IConfiguration configuration, ILogger logger)
    {
        var chains = configuration
            .GetSection("Scraping:Chains")
            .Get<List<ScrapingChainConfig>>() ?? [];

        if (chains.Count == 0) return;

        var seeded = 0;
        var updated = 0;

        foreach (var config in chains.Where(c => !string.IsNullOrWhiteSpace(c.Slug)))
        {
            var existing = await db.StoreChains
                .FirstOrDefaultAsync(sc => sc.Slug == config.Slug);

            if (existing is null)
            {
                db.StoreChains.Add(new StoreChain
                {
                    Id       = Guid.NewGuid(),
                    Slug     = config.Slug,
                    Name     = string.IsNullOrWhiteSpace(config.Name) ? config.Slug : config.Name,
                    BaseUrl  = config.BaseUrl ?? string.Empty,
                    IsActive = config.Enabled
                });
                seeded++;
            }
            else
            {
                // Only update IsActive; don't overwrite Name/BaseUrl set by admin
                if (existing.IsActive != config.Enabled)
                {
                    existing.IsActive = config.Enabled;
                    updated++;
                }
            }
        }

        if (seeded > 0 || updated > 0)
        {
            await db.SaveChangesAsync();
            logger.LogInformation(
                "StoreChainSeeder: seeded {Seeded} chain(s), updated {Updated} chain(s).",
                seeded, updated);
        }
    }
}
