using System.Net;
using NSubstitute;
using Savvori.WebApi.Scraping.Scrapers;
using Savvori.Shared;
using Microsoft.Extensions.Logging;

namespace Savvori.Web.Tests;

public class PingoDoceScraperTests
{
    private static string LoadFixture(string filename)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", filename);
        return File.ReadAllText(path);
    }

    private static (PingoDoceScraper Scraper, FakeHttpMessageHandler Handler) CreateScraper()
    {
        var html = LoadFixture("pingodoce_products.html");
        var handler = new FakeHttpMessageHandler();
        handler.SetupRoute("start=0", html);
        handler.SetupRoute("start=24", "<html><body></body></html>");

        var httpClient = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("pingodoce").Returns(httpClient);

        var logger = Substitute.For<ILogger<PingoDoceScraper>>();
        var scraper = new PingoDoceScraper(factory, logger) { ReattemptDelay = TimeSpan.Zero };
        return (scraper, handler);
    }

    [Fact]
    public async Task PingoDoce_Probe_Ok()
    {
        var (scraper, handler) = CreateScraper();
        handler.SetupRoute("cgid=ec_frutasevegetais_100", LoadFixture("pingodoce_products.html"));
        var r = await scraper.ProbeAsync(CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task PingoDoce_Probe_410_NotOk()
    {
        var (scraper, handler) = CreateScraper();
        handler.SetupRoute("start=0", "gone", HttpStatusCode.Gone);
        var r = await scraper.ProbeAsync(CancellationToken.None);
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task PingoDoce_FailedCategory_RecoveredAtEnd()
    {
        var (scraper, handler) = CreateScraper();
        handler.SetupRouteSequence("cgid=leite&format=ajax&start=0",
            (HttpStatusCode.Gone, "gone"), (HttpStatusCode.OK, LoadFixture("pingodoce_products.html")));
        var result = await scraper.ScrapeProductsAsync("leite", CancellationToken.None);
        var o = Assert.Single(result.Outcomes);
        Assert.Equal(OutcomeResult.Recovered, o.Result);
        Assert.NotEmpty(result.Products);
    }

    [Fact]
    public async Task PingoDoce_410OnFirstPage_OutcomeFailed()
    {
        var (scraper, handler) = CreateScraper();
        handler.SetupRoute("start=0", "gone", HttpStatusCode.Gone);

        var result = await scraper.ScrapeProductsAsync("leite", CancellationToken.None);

        Assert.Empty(result.Products);
        var o = Assert.Single(result.Outcomes);
        Assert.Equal(OutcomeKind.Category, o.Kind);
        Assert.Equal(OutcomeResult.Failed, o.Result);
        Assert.True(o.StatusCounts.ContainsKey(410));
    }

    [Fact]
    public async Task PingoDoce_HealthyFirstPage_OutcomeOk()
    {
        var (scraper, _) = CreateScraper();

        var result = await scraper.ScrapeProductsAsync("leite", CancellationToken.None);

        var o = Assert.Single(result.Outcomes);
        Assert.Equal(OutcomeResult.Ok, o.Result);
        Assert.True(o.TilesSeen > 0);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesProductsFromFixture()
    {
        var (scraper, _) = CreateScraper();
        var products = (await scraper.ScrapeProductsAsync("leite", CancellationToken.None)).Products;

        // 3 valid products (1 without GTM data is skipped)
        Assert.Equal(3, products.Count);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesNameAndBrand()
    {
        var (scraper, _) = CreateScraper();
        var products = (await scraper.ScrapeProductsAsync("leite", CancellationToken.None)).Products;

        var leite = products.First(p => p.ExternalId == "48150");
        Assert.Equal("Leite UHT Meio Gordo", leite.Name);
        Assert.Equal("Pingo Doce", leite.Brand);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesPrice()
    {
        var (scraper, _) = CreateScraper();
        var products = (await scraper.ScrapeProductsAsync("leite", CancellationToken.None)).Products;

        var leite = products.First(p => p.ExternalId == "48150");
        Assert.Equal(0.86m, leite.Price);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesUnitPriceFromTileText()
    {
        var (scraper, _) = CreateScraper();
        var products = (await scraper.ScrapeProductsAsync("leite", CancellationToken.None)).Products;

        var leite = products.First(p => p.ExternalId == "48150");
        Assert.Equal(0.86m, leite.UnitPrice);
        Assert.Equal(ProductUnit.L, leite.Unit);
        Assert.Equal(1.0m, leite.SizeValue);
    }

    [Fact]
    public async Task ScrapeProductsAsync_DetectsPromotion_WhenDiscountIsPositive()
    {
        var (scraper, _) = CreateScraper();
        var products = (await scraper.ScrapeProductsAsync("leite", CancellationToken.None)).Products;

        var mimosa = products.First(p => p.ExternalId == "41043");
        Assert.True(mimosa.IsPromotion);
        Assert.NotNull(mimosa.PromotionDescription);
    }

    [Fact]
    public async Task ScrapeProductsAsync_NoPromotion_WhenDiscountIsZero()
    {
        var (scraper, _) = CreateScraper();
        var products = (await scraper.ScrapeProductsAsync("leite", CancellationToken.None)).Products;

        var leite = products.First(p => p.ExternalId == "48150");
        Assert.False(leite.IsPromotion);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesCategory()
    {
        var (scraper, _) = CreateScraper();
        var products = (await scraper.ScrapeProductsAsync("leite", CancellationToken.None)).Products;

        var leite = products.First(p => p.ExternalId == "48150");
        Assert.Equal("Leite Meio Gordo e Gordo", leite.Category);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesGramProduct()
    {
        var (scraper, _) = CreateScraper();
        var products = (await scraper.ScrapeProductsAsync("leite", CancellationToken.None)).Products;

        var iogurte = products.First(p => p.ExternalId == "123456");
        Assert.Equal(ProductUnit.G, iogurte.Unit);
        Assert.Equal(500m, iogurte.SizeValue);
        Assert.Equal(3.98m, iogurte.UnitPrice);
    }

    [Fact]
    public async Task ScrapeProductsAsync_SkipsProductsWithNoGtmInfo()
    {
        var (scraper, _) = CreateScraper();
        var products = (await scraper.ScrapeProductsAsync("leite", CancellationToken.None)).Products;

        Assert.DoesNotContain(products, p => p.ExternalId == "9999");
    }

    [Fact]
    public async Task ScrapeProductsAsync_DeduplicatesProducts()
    {
        var (scraper, _) = CreateScraper();
        var products = (await scraper.ScrapeProductsAsync("leite", CancellationToken.None)).Products;

        var pids = products.Select(p => p.ExternalId).ToList();
        Assert.Equal(pids.Distinct().Count(), pids.Count);
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
        var logger = Substitute.For<ILogger<PingoDoceScraper>>();
        var scraper = new PingoDoceScraper(factory, logger);
        Assert.Equal("pingodoce", scraper.StoreChainSlug);
    }
}
