namespace Savvori.WebApi.Scraping.Scrapers;

/// <summary>
/// Stub scraper for Mercadona Portugal.
/// Mercadona PT does not have an online grocery store at this time.
/// They operate physical stores only in Portugal.
/// </summary>
public sealed class MercadonaScraper : BaseHttpScraper
{
    public override string StoreChainSlug => "mercadona";

    public MercadonaScraper(
        IHttpClientFactory httpClientFactory,
        ILogger<MercadonaScraper> logger)
        : base(httpClientFactory, logger, "mercadona")
    {
    }

    public override Task<IReadOnlyList<ScrapedProduct>> ScrapeProductsAsync(
        string? category = null, CancellationToken ct = default)
    {
        Logger.LogWarning("Mercadona scraper is not yet implemented — Mercadona PT has no online grocery store");
        return Task.FromResult<IReadOnlyList<ScrapedProduct>>([]);
    }

    public override Task<IReadOnlyList<ScrapedStoreLocation>> ScrapeStoreLocationsAsync(
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ScrapedStoreLocation>>([]);
    }
}
