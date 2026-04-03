using System.Net;
using NSubstitute;
using Savvori.WebApi.Scraping.Scrapers;
using Savvori.Shared;
using Microsoft.Extensions.Logging;

namespace Savvori.Web.Tests;

public class ContinenteScraperTests
{
    private static string LoadFixture(string filename)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", filename);
        return File.ReadAllText(path);
    }

    private static (ContinenteScraper Scraper, FakeHttpMessageHandler Handler) CreateScraper()
    {
        var html = LoadFixture("continente_products.html");
        var handler = new FakeHttpMessageHandler();
        // First page returns products, second page returns empty (stops pagination)
        handler.SetupRoute("start=0", html);
        handler.SetupRoute("start=48", "<html><body></body></html>");

        var httpClient = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("continente").Returns(httpClient);

        var logger = Substitute.For<ILogger<ContinenteScraper>>();
        var scraper = new ContinenteScraper(factory, logger);
        return (scraper, handler);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesProductsFromFixture()
    {
        var (scraper, _) = CreateScraper();

        var products = await scraper.ScrapeProductsAsync("frescos", CancellationToken.None);

        // 3 valid products (1 with price=0 is skipped)
        Assert.Equal(3, products.Count);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesNameAndBrand()
    {
        var (scraper, _) = CreateScraper();
        var products = await scraper.ScrapeProductsAsync("frescos", CancellationToken.None);

        var leite = products.First(p => p.ExternalId == "8696608");
        Assert.Equal("Leite UHT Magro sem Lactose Continente", leite.Name);
        Assert.Equal("Continente", leite.Brand);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesPrice()
    {
        var (scraper, _) = CreateScraper();
        var products = await scraper.ScrapeProductsAsync("frescos", CancellationToken.None);

        var leite = products.First(p => p.ExternalId == "8696608");
        Assert.Equal(1.08m, leite.Price);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesUnitPrice()
    {
        var (scraper, _) = CreateScraper();
        var products = await scraper.ScrapeProductsAsync("frescos", CancellationToken.None);

        var leite = products.First(p => p.ExternalId == "8696608");
        Assert.Equal(1.08m, leite.UnitPrice);
        Assert.Equal(ProductUnit.L, leite.Unit);
        Assert.Equal(1.0m, leite.SizeValue);
    }

    [Fact]
    public async Task ScrapeProductsAsync_DetectsPromotion()
    {
        var (scraper, _) = CreateScraper();
        var products = await scraper.ScrapeProductsAsync("frescos", CancellationToken.None);

        var mimosa = products.First(p => p.ExternalId == "2210947");
        Assert.True(mimosa.IsPromotion);
        Assert.Equal(0.9m, mimosa.Price);
    }

    [Fact]
    public async Task ScrapeProductsAsync_NonPromoProduct_IsNotPromotion()
    {
        var (scraper, _) = CreateScraper();
        var products = await scraper.ScrapeProductsAsync("frescos", CancellationToken.None);

        var leite = products.First(p => p.ExternalId == "8696608");
        Assert.False(leite.IsPromotion);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesCategory()
    {
        var (scraper, _) = CreateScraper();
        var products = await scraper.ScrapeProductsAsync("frescos", CancellationToken.None);

        var leite = products.First(p => p.ExternalId == "8696608");
        Assert.Equal("Leite sem Lactose", leite.Category);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesImageUrl()
    {
        var (scraper, _) = CreateScraper();
        var products = await scraper.ScrapeProductsAsync("frescos", CancellationToken.None);

        var leite = products.First(p => p.ExternalId == "8696608");
        Assert.NotNull(leite.ImageUrl);
        Assert.Contains("8696608", leite.ImageUrl);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesSourceUrl()
    {
        var (scraper, _) = CreateScraper();
        var products = await scraper.ScrapeProductsAsync("frescos", CancellationToken.None);

        var leite = products.First(p => p.ExternalId == "8696608");
        Assert.NotNull(leite.SourceUrl);
        Assert.Contains("8696608", leite.SourceUrl);
    }

    [Fact]
    public async Task ScrapeProductsAsync_SkipsProductsWithZeroPrice()
    {
        var (scraper, _) = CreateScraper();
        var products = await scraper.ScrapeProductsAsync("frescos", CancellationToken.None);

        Assert.DoesNotContain(products, p => p.ExternalId == "9999999");
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesGramProduct()
    {
        var (scraper, _) = CreateScraper();
        var products = await scraper.ScrapeProductsAsync("frescos", CancellationToken.None);

        var queijo = products.First(p => p.ExternalId == "1234567");
        Assert.Equal(ProductUnit.G, queijo.Unit);
        Assert.Equal(500m, queijo.SizeValue);
        Assert.Equal(4.98m, queijo.UnitPrice);
    }

    [Fact]
    public async Task ScrapeStoreLocationsAsync_ReturnsEmpty()
    {
        var (scraper, _) = CreateScraper();
        var locations = await scraper.ScrapeStoreLocationsAsync(CancellationToken.None);
        Assert.Empty(locations);
    }

    [Fact]
    public void StoreChainSlug_IsCorrect()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        var logger = Substitute.For<ILogger<ContinenteScraper>>();
        var scraper = new ContinenteScraper(factory, logger);
        Assert.Equal("continente", scraper.StoreChainSlug);
    }
}
