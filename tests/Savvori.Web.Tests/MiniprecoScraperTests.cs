using System.Net;
using NSubstitute;
using Savvori.WebApi.Scraping.Scrapers;
using Savvori.Shared;
using Microsoft.Extensions.Logging;

namespace Savvori.Web.Tests;

public class MiniprecoScraperTests
{
    private static string LoadFixture(string filename)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", filename);
        return File.ReadAllText(path);
    }

    private static (MiniprecoScraper Scraper, FakeHttpMessageHandler Handler) CreateScraper()
    {
        var html = LoadFixture("minipreco_products.html");
        var handler = new FakeHttpMessageHandler();
        // Page 1 returns products, page 2 returns empty (stops pagination)
        handler.SetupRoute("page=1", html);
        handler.SetupRoute("page=2", "<html><body><div id='productgridcontainer'></div></body></html>");

        var httpClient = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("minipreco").Returns(httpClient);

        var logger = Substitute.For<ILogger<MiniprecoScraper>>();
        return (new MiniprecoScraper(factory, logger), handler);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesProductsFromFixture()
    {
        var (scraper, _) = CreateScraper();
        var products = await scraper.ScrapeProductsAsync("laticinios", CancellationToken.None);

        // 4 valid products; 1 skipped (no /p/ in href)
        Assert.Equal(4, products.Count);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesProductId()
    {
        var (scraper, _) = CreateScraper();
        var products = await scraper.ScrapeProductsAsync("laticinios", CancellationToken.None);

        Assert.Contains(products, p => p.ExternalId == "6692");
        Assert.Contains(products, p => p.ExternalId == "195043");
        Assert.Contains(products, p => p.ExternalId == "11111");
        Assert.Contains(products, p => p.ExternalId == "262214");
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesName()
    {
        var (scraper, _) = CreateScraper();
        var products = await scraper.ScrapeProductsAsync("laticinios", CancellationToken.None);

        var leite = products.First(p => p.ExternalId == "6692");
        Assert.Contains("MIMOSA", leite.Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesPrice()
    {
        var (scraper, _) = CreateScraper();
        var products = await scraper.ScrapeProductsAsync("laticinios", CancellationToken.None);

        var leite = products.First(p => p.ExternalId == "6692");
        Assert.Equal(1.00m, leite.Price);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesUnitPrice()
    {
        var (scraper, _) = CreateScraper();
        var products = await scraper.ScrapeProductsAsync("laticinios", CancellationToken.None);

        var leite = products.First(p => p.ExternalId == "6692");
        Assert.Equal(1.00m, leite.UnitPrice);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesUnitPriceWithKgUnit()
    {
        var (scraper, _) = CreateScraper();
        var products = await scraper.ScrapeProductsAsync("laticinios", CancellationToken.None);

        var arroz = products.First(p => p.ExternalId == "11111");
        Assert.Equal(0.89m, arroz.UnitPrice);
    }

    [Fact]
    public async Task ScrapeProductsAsync_DetectsPromotion()
    {
        var (scraper, _) = CreateScraper();
        var products = await scraper.ScrapeProductsAsync("laticinios", CancellationToken.None);

        var semLactose = products.First(p => p.ExternalId == "195043");
        Assert.True(semLactose.IsPromotion);
    }

    [Fact]
    public async Task ScrapeProductsAsync_NoPromotion_WhenNoBadge()
    {
        var (scraper, _) = CreateScraper();
        var products = await scraper.ScrapeProductsAsync("laticinios", CancellationToken.None);

        var leite = products.First(p => p.ExternalId == "6692");
        Assert.False(leite.IsPromotion);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesCategory()
    {
        var (scraper, _) = CreateScraper();
        var products = await scraper.ScrapeProductsAsync("laticinios", CancellationToken.None);

        var leite = products.First(p => p.ExternalId == "6692");
        // Category is second-to-last segment before /p/
        Assert.Contains("leite", leite.Category, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScrapeProductsAsync_SkipsItemsWithNoProductId()
    {
        var (scraper, _) = CreateScraper();
        var products = await scraper.ScrapeProductsAsync("laticinios", CancellationToken.None);

        // The item with href "/produtos/categoria-apenas" has no /p/ pattern
        Assert.Equal(4, products.Count);
    }

    [Fact]
    public async Task ScrapeProductsAsync_StoreChainSlugIsMinipreco()
    {
        var scraper = CreateScraper().Scraper;
        Assert.Equal("minipreco", scraper.StoreChainSlug);
    }

    [Fact]
    public async Task ScrapeStoreLocationsAsync_ReturnsEmptyList()
    {
        var (scraper, _) = CreateScraper();
        var locations = await scraper.ScrapeStoreLocationsAsync(CancellationToken.None);
        Assert.Empty(locations);
    }
}
