using Microsoft.EntityFrameworkCore;
using Savvori.Shared;

namespace Savvori.WebApi.Scraping;

/// <summary>
/// Handles canonical-product matching for a <see cref="StoreProduct"/>.
/// Used by <see cref="ScraperResultProcessor"/> during scraping and by the
/// admin rematch endpoint to repair Unmatched / Failed products without
/// creating duplicate canonical products.
/// </summary>
public sealed class ProductMatcher
{
    private readonly SavvoriDbContext _db;

    public ProductMatcher(SavvoriDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Tries Tier 1 (EAN) and Tier 2 (brand + normalised name + size + unit) matches.
    /// Updates <paramref name="storeProduct"/> in place when a match is found.
    /// Does NOT create new canonical products — that is only done during scraping.
    /// </summary>
    /// <returns><c>true</c> if a canonical product was found and linked.</returns>
    public async Task<bool> TryMatchAsync(
        StoreProduct storeProduct,
        string? ean,
        string normalizedName,
        decimal? sizeValue,
        ProductUnit unit,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // Tier 1: EAN
        if (!string.IsNullOrEmpty(ean))
        {
            var byEan = await _db.Products.FirstOrDefaultAsync(p => p.EAN == ean, ct);
            if (byEan is not null)
            {
                storeProduct.CanonicalProductId = byEan.Id;
                storeProduct.CanonicalProduct = byEan;
                storeProduct.MatchStatus = MatchStatus.AutoMatched;
                storeProduct.MatchMethod = "ean";
                storeProduct.MatchedAt = now;
                return true;
            }
        }

        // Tier 2: brand + normalised name + size + unit
        var byNameBrand = await _db.Products.FirstOrDefaultAsync(p =>
            p.NormalizedName == normalizedName &&
            p.SizeValue == sizeValue &&
            p.Unit == unit &&
            (p.Brand == null || storeProduct.Brand == null || p.Brand == storeProduct.Brand), ct);

        if (byNameBrand is not null)
        {
            storeProduct.CanonicalProductId = byNameBrand.Id;
            storeProduct.CanonicalProduct = byNameBrand;
            storeProduct.MatchStatus = MatchStatus.AutoMatched;
            storeProduct.MatchMethod = "brand+name+size+unit";
            storeProduct.MatchedAt = now;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Creates and persists a new canonical <see cref="Product"/> then links it to
    /// <paramref name="storeProduct"/>.
    /// </summary>
    public Product CreateCanonical(
        StoreProduct storeProduct,
        string name,
        string? brand,
        string? rawCategory,
        string normalizedName,
        decimal? sizeValue,
        ProductUnit unit,
        Guid? categoryId,
        string? imageUrl)
    {
        var product = new Product
        {
            Id = Guid.NewGuid(),
            Name = name,
            Brand = brand,
            Category = rawCategory,
            CategoryId = categoryId,
            NormalizedName = normalizedName,
            EAN = storeProduct.EAN,
            Unit = unit,
            SizeValue = sizeValue,
            ImageUrl = imageUrl
        };
        _db.Products.Add(product);

        storeProduct.CanonicalProductId = product.Id;
        storeProduct.CanonicalProduct = product;
        storeProduct.MatchStatus = MatchStatus.AutoMatched;
        storeProduct.MatchMethod = "created-new";
        storeProduct.MatchedAt = DateTime.UtcNow;

        return product;
    }
}
