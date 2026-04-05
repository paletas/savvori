using Microsoft.EntityFrameworkCore;
using Savvori.Shared;

namespace Savvori.WebApi.Scraping;

/// <summary>
/// Normalizes scraped data and upserts StoreProducts, StoreProductPrices,
/// and canonical Products into the database.
/// </summary>
public sealed class ScraperResultProcessor
{
    private readonly SavvoriDbContext _db;
    private readonly ILogger<ScraperResultProcessor> _logger;
    private readonly ProductMatcher _matcher;

    // In-memory cache populated once per ProcessProductsAsync call: slug -> Guid
    private Dictionary<string, Guid> _categoryCache = [];

    public ScraperResultProcessor(SavvoriDbContext db, ILogger<ScraperResultProcessor> logger)
    {
        _db = db;
        _logger = logger;
        _matcher = new ProductMatcher(db);
    }

    /// <summary>
    /// Processes scraped products for a given store chain.
    /// Upserts StoreProduct records, adds StoreProductPrice history,
    /// and runs canonical product matching.
    /// </summary>
    public async Task<int> ProcessProductsAsync(
        string storeChainSlug,
        IReadOnlyList<ScrapedProduct> scraped,
        CancellationToken ct = default)
    {
        var chain = await _db.StoreChains.FirstOrDefaultAsync(sc => sc.Slug == storeChainSlug, ct);
        if (chain is null)
        {
            _logger.LogError("StoreChain '{Slug}' not found. Cannot process products.", storeChainSlug);
            return 0;
        }

        // Cache category slugs for fast lookup during this processing run
        _categoryCache = await _db.ProductCategories
            .ToDictionaryAsync(pc => pc.Slug, pc => pc.Id, ct);

        var scrapedExternalIds = scraped.Select(s => s.ExternalId).ToHashSet();
        var chainId = chain.Id;
        var processedCount = 0;
        var skippedCount = 0;

        foreach (var item in scraped)
        {
            try
            {
                await ProcessOneProductAsync(item, chain, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to process product '{Name}' (ExternalId: {ExternalId}) from {Chain}",
                    item.Name, item.ExternalId, storeChainSlug);
                _db.ChangeTracker.Clear();
                skippedCount++;
                continue;
            }

            try
            {
                await _db.SaveChangesAsync(ct);
                _db.ChangeTracker.Clear();
                processedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Save failed for product '{Name}' (ExternalId: {ExternalId}) from {Chain}",
                    item.Name, item.ExternalId, storeChainSlug);
                _db.ChangeTracker.Clear();
                skippedCount++;
            }
        }

        // Mark StoreProducts not seen in this scrape as inactive.
        // ChangeTracker is clear here so queries go straight to the DB.
        var staleProducts = await _db.StoreProducts
            .Where(sp => sp.StoreChainId == chainId &&
                         sp.IsActive &&
                         !scrapedExternalIds.Contains(sp.ExternalId))
            .ToListAsync(ct);
        foreach (var stale in staleProducts)
            stale.IsActive = false;

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Processed {Count}/{Total} products for {Chain}. Skipped {Skipped}. Marked {Stale} inactive.",
            processedCount, scraped.Count, storeChainSlug, skippedCount, staleProducts.Count);
        return processedCount;
    }

    /// <summary>
    /// Processes scraped store locations, upserting them for the given chain.
    /// </summary>
    public async Task ProcessStoreLocationsAsync(
        string storeChainSlug,
        IReadOnlyList<ScrapedStoreLocation> scraped,
        CancellationToken ct = default)
    {
        var chain = await _db.StoreChains.FirstOrDefaultAsync(sc => sc.Slug == storeChainSlug, ct);
        if (chain is null)
        {
            _logger.LogError("StoreChain '{Slug}' not found. Cannot process store locations.", storeChainSlug);
            return;
        }

        foreach (var location in scraped)
        {
            var existing = await _db.Stores
                .FirstOrDefaultAsync(s =>
                    s.StoreChainId == chain.Id &&
                    s.PostalCode == location.PostalCode &&
                    s.Name == location.Name, ct);

            if (existing is not null)
            {
                existing.Address = location.Address;
                existing.City = location.City;
                existing.Latitude = location.Latitude;
                existing.Longitude = location.Longitude;
                existing.IsActive = true;
            }
            else
            {
                _db.Stores.Add(new Store
                {
                    Id = Guid.NewGuid(),
                    StoreChainId = chain.Id,
                    Name = location.Name,
                    Address = location.Address,
                    PostalCode = location.PostalCode,
                    City = location.City,
                    Latitude = location.Latitude,
                    Longitude = location.Longitude,
                    IsActive = true
                });
            }
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Processed {Count} store locations for {Chain}",
            scraped.Count, storeChainSlug);
    }

    private async Task ProcessOneProductAsync(
        ScrapedProduct scraped,
        StoreChain chain,
        CancellationToken ct)
    {
        var normalized = ProductNormalizer.Normalize(scraped.Name);
        var sizeUnit = ProductNormalizer.ExtractSizeAndUnit(scraped.Name);
        var sizeValue = scraped.SizeValue ?? sizeUnit?.SizeValue;
        var unit = sizeUnit?.Unit ?? scraped.Unit;
        var unitPrice = scraped.UnitPrice ?? ProductNormalizer.ComputeUnitPrice(scraped.Price, unit, sizeValue);
        var now = DateTime.UtcNow;

        // Upsert StoreProduct by (StoreChainId, ExternalId)
        var storeProduct = await _db.StoreProducts
            .FirstOrDefaultAsync(sp => sp.StoreChainId == chain.Id && sp.ExternalId == scraped.ExternalId, ct);

        if (storeProduct is null)
        {
            storeProduct = new StoreProduct
            {
                Id = Guid.NewGuid(),
                StoreChainId = chain.Id,
                ExternalId = scraped.ExternalId,
                Name = scraped.Name,
                NormalizedName = normalized,
                Brand = scraped.Brand,
                EAN = scraped.EAN,
                ImageUrl = scraped.ImageUrl,
                SourceUrl = scraped.SourceUrl,
                Unit = unit,
                SizeValue = sizeValue,
                FirstSeen = now,
                LastScraped = now,
                IsActive = true,
                MatchStatus = MatchStatus.Unmatched
            };
            _db.StoreProducts.Add(storeProduct);
        }
        else
        {
            storeProduct.Name = scraped.Name;
            storeProduct.NormalizedName = normalized;
            storeProduct.Brand = scraped.Brand;
            if (!string.IsNullOrEmpty(scraped.EAN))
                storeProduct.EAN = scraped.EAN;
            storeProduct.ImageUrl = scraped.ImageUrl;
            storeProduct.SourceUrl = scraped.SourceUrl;
            storeProduct.Unit = unit;
            storeProduct.SizeValue = sizeValue;
            storeProduct.LastScraped = now;
            storeProduct.IsActive = true;
        }

        // Run or re-run canonical matching:
        // - If unmatched, always try to match.
        // - If auto-matched by name but we now have an EAN, re-evaluate (self-healing).
        var needsMatch = storeProduct.MatchStatus == MatchStatus.Unmatched ||
                         (storeProduct.MatchStatus == MatchStatus.AutoMatched &&
                          storeProduct.MatchMethod != "ean" &&
                          !string.IsNullOrEmpty(storeProduct.EAN));

        if (needsMatch)
        {
            var categoryId = ResolveCategoryId(scraped.Category);
            await MatchCanonicalProductAsync(storeProduct, scraped, normalized, sizeValue, unit, categoryId, ct);
        }

        // Backfill CategoryId on canonical product if it was created without one
        if (storeProduct.CanonicalProductId is not null)
        {
            var canonical = storeProduct.CanonicalProduct ??
                            await _db.Products.FindAsync([storeProduct.CanonicalProductId.Value], ct);
            if (canonical is not null && canonical.CategoryId is null)
            {
                var catId = ResolveCategoryId(scraped.Category);
                if (catId is not null)
                    canonical.CategoryId = catId;
            }
        }

        // Mark previous latest price as historical
        var latestPrice = await _db.StoreProductPrices
            .FirstOrDefaultAsync(spp => spp.StoreProductId == storeProduct.Id && spp.IsLatest, ct);
        if (latestPrice is not null)
            latestPrice.IsLatest = false;

        // Add new price snapshot
        _db.StoreProductPrices.Add(new StoreProductPrice
        {
            Id = Guid.NewGuid(),
            StoreProductId = storeProduct.Id,
            Price = scraped.Price,
            UnitPrice = unitPrice,
            Currency = "EUR",
            IsPromotion = scraped.IsPromotion,
            PromotionDescription = scraped.PromotionDescription,
            IsLatest = true,
            ScrapedAt = now
        });
    }

    private Guid? ResolveCategoryId(string? scrapedCategory)
    {
        var slug = CategoryMapper.MapToSlug(scrapedCategory);
        if (slug is null) return null;
        return _categoryCache.TryGetValue(slug, out var id) ? id : null;
    }

    private async Task MatchCanonicalProductAsync(
        StoreProduct storeProduct,
        ScrapedProduct scraped,
        string normalized,
        decimal? sizeValue,
        ProductUnit unit,
        Guid? categoryId,
        CancellationToken ct)
    {
        // Tier 1 + 2 via shared matcher
        var matched = await _matcher.TryMatchAsync(storeProduct, scraped.EAN, normalized, sizeValue, unit, ct);
        if (matched) return;

        // Tier 3: Create new canonical product (only during scraping, not admin rematch)
        _matcher.CreateCanonical(
            storeProduct,
            scraped.Name, scraped.Brand, scraped.Category,
            normalized, sizeValue, unit, categoryId, scraped.ImageUrl);
    }
}