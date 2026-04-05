using Microsoft.Extensions.Logging.Abstractions;
using Savvori.WebApi.Scraping.Scrapers;

namespace Savvori.Web.Tests;

/// <summary>
/// Live integration tests that hit real store websites.
/// Marked with [Trait("Category", "Live")] — excluded from normal CI runs.
/// Each test scrapes a single small category to keep execution time reasonable.
/// </summary>
public sealed class LiveScraperTests
{
    private static IHttpClientFactory BuildFactory(string name, string baseUrl)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromMinutes(3)
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "pt-PT,pt;q=0.9");

        return new NamedClientFactory(name, client);
    }

    // ── Auchan ────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Live")]
    public async Task Auchan_ScrapesAtLeastOneProduct()
    {
        var factory = BuildFactory("auchan", "https://www.auchan.pt");
        var scraper = new AuchanScraper(factory, NullLogger<AuchanScraper>.Instance);

        var products = await scraper.ScrapeProductsAsync("ovos", CancellationToken.None);

        Assert.NotEmpty(products);
        Assert.All(products, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.False(string.IsNullOrWhiteSpace(p.ExternalId));
            Assert.True(p.Price > 0m);
        });
    }

    // ── Continente ────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Live")]
    public async Task Continente_ScrapesAtLeastOneProduct()
    {
        var factory = BuildFactory("continente", "https://www.continente.pt");
        var scraper = new ContinenteScraper(factory, NullLogger<ContinenteScraper>.Instance);

        var products = await scraper.ScrapeProductsAsync("laticinios-e-ovos/leite", CancellationToken.None);

        Assert.NotEmpty(products);
        Assert.All(products, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.False(string.IsNullOrWhiteSpace(p.ExternalId));
            Assert.True(p.Price > 0m);
        });
    }

    // ── Pingo Doce ────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Live")]
    public async Task PingoDoce_ScrapesAtLeastOneProduct()
    {
        var factory = BuildFactory("pingodoce", "https://www.pingodoce.pt");
        var scraper = new PingoDoceScraper(factory, NullLogger<PingoDoceScraper>.Instance);

        var products = await scraper.ScrapeProductsAsync("ec_leitebebidasvegetais_900", CancellationToken.None);

        Assert.NotEmpty(products);
        Assert.All(products, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.False(string.IsNullOrWhiteSpace(p.ExternalId));
            Assert.True(p.Price > 0m);
        });
    }

    // ── Minipreço ─────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Live")]
    public async Task Minipreco_ScrapesAtLeastOneProduct()
    {
        var factory = BuildFactory("minipreco", "https://www.minipreco.pt");
        var scraper = new MiniprecoScraper(factory, NullLogger<MiniprecoScraper>.Instance);

        var products = await scraper.ScrapeProductsAsync("leite", CancellationToken.None);

        Assert.NotEmpty(products);
        Assert.All(products, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.False(string.IsNullOrWhiteSpace(p.ExternalId));
            Assert.True(p.Price > 0m);
        });
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private sealed class NamedClientFactory(string name, HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string requestedName) =>
            requestedName == name
                ? client
                : throw new InvalidOperationException($"No client registered for '{requestedName}'.");
    }
}
