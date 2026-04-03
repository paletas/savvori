namespace Savvori.WebApi.Scraping.Scrapers;

/// <summary>
/// Stub scraper for Intermarché Portugal.
/// Intermarché PT does not have an online grocery store with product listings and prices.
/// Their website only offers flyers (folhetos) and store information.
/// </summary>
public sealed class InterarcheScraper : BaseHttpScraper
{
    public override string StoreChainSlug => "intermarche";

    public InterarcheScraper(
        IHttpClientFactory httpClientFactory,
        ILogger<InterarcheScraper> logger)
        : base(httpClientFactory, logger, "intermarche")
    {
    }

    public override Task<IReadOnlyList<ScrapedProduct>> ScrapeProductsAsync(
        string? category = null, CancellationToken ct = default)
    {
        Logger.LogWarning("Intermarché scraper is not yet implemented — Intermarché PT has no online product catalog");
        return Task.FromResult<IReadOnlyList<ScrapedProduct>>([]);
    }

    public override Task<IReadOnlyList<ScrapedStoreLocation>> ScrapeStoreLocationsAsync(
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ScrapedStoreLocation>>([]);
    }
}
