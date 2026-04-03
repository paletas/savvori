using Savvori.Shared;

namespace Savvori.WebApi.Scraping;

public interface IStoreScraper
{
    string StoreChainSlug { get; }

    Task<IReadOnlyList<ScrapedProduct>> ScrapeProductsAsync(
        string? category = null, CancellationToken ct = default);

    Task<IReadOnlyList<ScrapedStoreLocation>> ScrapeStoreLocationsAsync(
        CancellationToken ct = default);
}

public record ScrapedProduct(
    string Name,
    string? Brand,
    string? Category,
    decimal Price,
    decimal? UnitPrice,
    string? EAN,
    string ExternalId,
    string? ImageUrl,
    string? SourceUrl,
    bool IsPromotion,
    string? PromotionDescription,
    ProductUnit Unit,
    decimal? SizeValue
);

public record ScrapedStoreLocation(
    string Name,
    string? Address,
    string? PostalCode,
    string? City,
    double? Latitude,
    double? Longitude
);
