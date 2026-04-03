using Microsoft.EntityFrameworkCore;
using Savvori.Shared;

namespace Savvori.WebApi.Scraping;

/// <summary>
/// Normalizes scraped data and upserts products, prices, and store locations into the database.
/// </summary>
public sealed class ScraperResultProcessor
{
    private readonly SavvoriDbContext _db;
    private readonly ILogger<ScraperResultProcessor> _logger;

    // In-memory cache populated once per ProcessProductsAsync call: slug → Guid
    private Dictionary<string, Guid> _categoryCache = [];

    public ScraperResultProcessor(SavvoriDbContext db, ILogger<ScraperResultProcessor> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Processes scraped products for a given store chain, upserting canonical products and prices.
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

        var stores = await _db.Stores
            .Where(s => s.StoreChainId == chain.Id && s.IsActive)
            .ToListAsync(ct);

        // Cache category slugs for fast lookup during this processing run
        _categoryCache = await _db.ProductCategories
            .ToDictionaryAsync(pc => pc.Slug, pc => pc.Id, ct);

        var processedCount = 0;

        foreach (var item in scraped)
        {
            try
            {
                await ProcessOneProductAsync(item, chain, stores, ct);
                processedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process product '{Name}' from {Chain}",
                    item.Name, storeChainSlug);
            }
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Processed {Count}/{Total} products for {Chain}",
            processedCount, scraped.Count, storeChainSlug);
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
        IReadOnlyList<Store> chainStores,
        CancellationToken ct)
    {
        var normalized = ProductNormalizer.Normalize(scraped.Name);
        var sizeUnit = ProductNormalizer.ExtractSizeAndUnit(scraped.Name);
        var sizeValue = scraped.SizeValue ?? sizeUnit?.SizeValue;
        var unit = sizeUnit?.Unit ?? scraped.Unit;
        var unitPrice = scraped.UnitPrice ?? ProductNormalizer.ComputeUnitPrice(scraped.Price, unit, sizeValue);

        // Resolve CategoryId from the scraped category string
        var categoryId = ResolveCategoryId(scraped.Category);

        // Resolve canonical product
        var product = await FindOrCreateProductAsync(scraped, normalized, sizeValue, unit, categoryId, ct);

        // If an existing product has no CategoryId but we now have one, backfill it
        if (product.CategoryId is null && categoryId is not null)
            product.CategoryId = categoryId;

        // Upsert ProductStoreLink so we can map back to scraped product later
        await UpsertStoreLinkAsync(product, chain, scraped, ct);

        // Mark existing latest prices as historical for this product + all chain stores
        var chainStoreIds = chainStores.Select(s => s.Id).ToHashSet();
        var latestPrices = await _db.ProductPrices
            .Where(pp => pp.ProductId == product.Id && chainStoreIds.Contains(pp.StoreId) && pp.IsLatest)
            .ToListAsync(ct);
        foreach (var old in latestPrices) old.IsLatest = false;

        // Add a new price record per active store of this chain (scraped price applies chain-wide)
        foreach (var store in chainStores)
        {
            _db.ProductPrices.Add(new ProductPrice
            {
                Id = Guid.NewGuid(),
                ProductId = product.Id,
                StoreId = store.Id,
                Price = scraped.Price,
                UnitPrice = unitPrice,
                Currency = "EUR",
                IsPromotion = scraped.IsPromotion,
                PromotionDescription = scraped.PromotionDescription,
                SourceUrl = scraped.SourceUrl,
                IsLatest = true,
                LastUpdated = DateTime.UtcNow
            });
        }
    }

    private Guid? ResolveCategoryId(string? scrapedCategory)
    {
        var slug = CategoryMapper.MapToSlug(scrapedCategory);
        if (slug is null) return null;
        return _categoryCache.TryGetValue(slug, out var id) ? id : null;
    }

    private async Task<Product> FindOrCreateProductAsync(
        ScrapedProduct scraped,
        string normalized,
        decimal? sizeValue,
        ProductUnit unit,
        Guid? categoryId,
        CancellationToken ct)
    {
        // Tier 1: EAN match (most reliable)
        if (!string.IsNullOrEmpty(scraped.EAN))
        {
            var byEan = await _db.Products.FirstOrDefaultAsync(p => p.EAN == scraped.EAN, ct);
            if (byEan is not null) return byEan;
        }

        // Tier 2: Normalized name + brand + size match
        var byName = await _db.Products.FirstOrDefaultAsync(p =>
            p.NormalizedName == normalized &&
            p.SizeValue == sizeValue &&
            p.Unit == unit, ct);
        if (byName is not null) return byName;

        // Tier 3: Create new product
        var product = new Product
        {
            Id = Guid.NewGuid(),
            Name = scraped.Name,
            Brand = scraped.Brand,
            Category = scraped.Category,
            CategoryId = categoryId,
            NormalizedName = normalized,
            EAN = scraped.EAN,
            Unit = unit,
            SizeValue = sizeValue,
            ImageUrl = scraped.ImageUrl
        };
        _db.Products.Add(product);
        return product;
    }

    private async Task UpsertStoreLinkAsync(
        Product product,
        StoreChain chain,
        ScrapedProduct scraped,
        CancellationToken ct)
    {
        var link = await _db.ProductStoreLinks.FirstOrDefaultAsync(psl =>
            psl.StoreChainId == chain.Id && psl.ExternalId == scraped.ExternalId, ct);

        if (link is not null)
        {
            link.ProductId = product.Id;
            link.SourceUrl = scraped.SourceUrl;
            link.LastSeen = DateTime.UtcNow;
        }
        else
        {
            _db.ProductStoreLinks.Add(new ProductStoreLink
            {
                Id = Guid.NewGuid(),
                ProductId = product.Id,
                StoreChainId = chain.Id,
                ExternalId = scraped.ExternalId,
                SourceUrl = scraped.SourceUrl,
                LastSeen = DateTime.UtcNow
            });
        }
    }
}
