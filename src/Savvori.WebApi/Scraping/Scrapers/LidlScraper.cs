namespace Savvori.WebApi.Scraping.Scrapers;

/// <summary>
/// Stub scraper for Lidl Portugal.
/// Lidl PT does not currently expose a scrapable online product catalog with prices.
/// Their website is heavily JS-rendered and products are marketed as "Em loja" (in-store) only.
/// This stub is registered to allow future implementation without changing the DI registration.
/// </summary>
public sealed class LidlScraper : BaseHttpScraper
{
    public override string StoreChainSlug => "lidl";

    public LidlScraper(
        IHttpClientFactory httpClientFactory,
        ILogger<LidlScraper> logger)
        : base(httpClientFactory, logger, "lidl")
    {
    }

    public override Task<IReadOnlyList<ScrapedProduct>> ScrapeProductsAsync(
        string? category = null, CancellationToken ct = default)
    {
        Logger.LogWarning("Lidl scraper is not yet implemented — Lidl PT has no scrapable online grocery catalog");
        return Task.FromResult<IReadOnlyList<ScrapedProduct>>([]);
    }

    public override Task<IReadOnlyList<ScrapedStoreLocation>> ScrapeStoreLocationsAsync(
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ScrapedStoreLocation>>([]);
    }
}
