using System.Net;
using NSubstitute;
using Savvori.WebApi.Scraping.Scrapers;
using Savvori.Shared;
using Microsoft.Extensions.Logging;

namespace Savvori.Web.Tests;

public class LidlScraperTests
{
    private static LidlScraper NewScraper(FakeHttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("lidl").Returns(new HttpClient(handler));
        return new LidlScraper(factory, Substitute.For<ILogger<LidlScraper>>()) { ReattemptDelay = TimeSpan.Zero, RequestSpacing = TimeSpan.Zero };
    }

    private static string Fixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lidl_search.json"));

    [Fact]
    public async Task Lidl_Probe_Ok()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetupRoute("q/api/search", Fixture());
        var r = await NewScraper(handler).ProbeAsync(CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Lidl_Probe_NoItems_NotOk()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetupRoute("q/api/search", "{\"items\":[]}");
        var r = await NewScraper(handler).ProbeAsync(CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("no product tiles", r.Message);
    }

    [Fact]
    public async Task Lidl_Probe_NotJson_NotOk()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetupRoute("q/api/search", "not json");
        var r = await NewScraper(handler).ProbeAsync(CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("unparseable", r.Message);
    }

    [Fact]
    public async Task Lidl_FailedQuery_RecoveredAtEnd()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetDefaultResponse(Fixture());
        handler.SetupRouteSequence("q=leite&offset=0", (HttpStatusCode.Gone, "gone"), (HttpStatusCode.OK, Fixture()));
        var result = await NewScraper(handler).ScrapeProductsAsync("leite", CancellationToken.None);
        var o = Assert.Single(result.Outcomes);
        Assert.Equal(OutcomeResult.Recovered, o.Result);
        Assert.NotEmpty(result.Products);
    }

    private static LidlScraper CreateScraper()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lidl_search.json"));
        var handler = new FakeHttpMessageHandler();
        handler.SetDefaultResponse(json);

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("lidl").Returns(new HttpClient(handler));

        return new LidlScraper(factory, Substitute.For<ILogger<LidlScraper>>()) { ReattemptDelay = TimeSpan.Zero, RequestSpacing = TimeSpan.Zero };
    }

    private static LidlScraper CreateScraper(FakeHttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("lidl").Returns(new HttpClient(handler));
        return new LidlScraper(factory, Substitute.For<ILogger<LidlScraper>>()) { ReattemptDelay = TimeSpan.Zero, RequestSpacing = TimeSpan.Zero };
    }

    [Fact]
    public async Task Lidl_410OnFirstPage_OutcomeFailed()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lidl_search.json"));
        var handler = new FakeHttpMessageHandler();
        handler.SetDefaultResponse(json);
        handler.SetupRoute("q=&offset=0", "gone", HttpStatusCode.Gone);

        var result = await CreateScraper(handler).ScrapeProductsAsync(null, CancellationToken.None);

        var o = result.Outcomes.First();
        Assert.Equal(OutcomeKind.Term, o.Kind);
        Assert.Equal("(empty)", o.Key);
        Assert.Equal(OutcomeResult.Failed, o.Result);
        Assert.True(o.StatusCounts.ContainsKey(410));
    }

    [Fact]
    public async Task Lidl_HealthyFirstPage_OutcomeOk()
    {
        var result = await CreateScraper().ScrapeProductsAsync("leite", CancellationToken.None);

        var o = Assert.Single(result.Outcomes);
        Assert.Equal(OutcomeResult.Ok, o.Result);
        Assert.True(o.TilesSeen > 0);
    }

    [Fact]
    public async Task Lidl_NonFoodOnlyPage_IsNotMissingFields()
    {
        const string json = """
            {"numFound":1,"items":[{"gridbox":{"data":{"category":"Non-Food","itemId":20000001,"fullTitle":"Martelo","price":{"price":9.99}}}}]}
            """;
        var handler = new FakeHttpMessageHandler();
        handler.SetDefaultResponse(json);

        var result = await CreateScraper(handler).ScrapeProductsAsync("martelo", CancellationToken.None);

        Assert.Empty(result.Products);
        var o = Assert.Single(result.Outcomes);
        Assert.Equal(OutcomeResult.Ok, o.Result);
    }

    [Fact]
    public async Task ScrapeProductsAsync_KeepsOnlyPricedFoodItems()
    {
        var products = (await CreateScraper().ScrapeProductsAsync(null, CancellationToken.None)).Products;

        // Non-food and price-less items are skipped
        Assert.Equal(2, products.Count);
        Assert.DoesNotContain(products, p => p.ExternalId == "20000001");
        Assert.DoesNotContain(products, p => p.ExternalId == "11500002");
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesPromotedProduct()
    {
        var products = (await CreateScraper().ScrapeProductsAsync(null, CancellationToken.None)).Products;

        var leite = products.First(p => p.ExternalId == "11038070");
        Assert.Equal("Mimosa Leite Meio Gordo", leite.Name);
        Assert.Equal("Mimosa", leite.Brand);
        Assert.Equal(0.89m, leite.Price);
        Assert.True(leite.IsPromotion);
        Assert.Equal("-11%", leite.PromotionDescription);
        Assert.Equal("Leite e natas", leite.Category);
        Assert.Equal(ProductUnit.L, leite.Unit);
        Assert.Equal("https://www.lidl.pt/p/mimosa-leite-meio-gordo/p11038070", leite.SourceUrl);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesBasePriceAsUnitPrice()
    {
        var products = (await CreateScraper().ScrapeProductsAsync(null, CancellationToken.None)).Products;

        var agros = products.First(p => p.ExternalId == "11500001");
        Assert.Equal(2.8m, agros.Price);
        Assert.Equal(1.75m, agros.UnitPrice);
        Assert.Equal(ProductUnit.L, agros.Unit);
        Assert.False(agros.IsPromotion);
        Assert.Null(agros.Category);
    }

    [Fact]
    public async Task ScrapeStoreLocationsAsync_ReturnsEmptyList()
    {
        Assert.Empty(await CreateScraper().ScrapeStoreLocationsAsync(CancellationToken.None));
    }
}
