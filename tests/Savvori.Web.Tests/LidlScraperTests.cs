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
    public async Task Lidl_NonFoodItem_IsParsed()
    {
        const string json = """
            {"numFound":1,"items":[{"gridbox":{"data":{"category":"Non-Food","itemId":20000001,"fullTitle":"Martelo","price":{"price":9.99}}}}]}
            """;
        var handler = new FakeHttpMessageHandler();
        handler.SetDefaultResponse(json);

        var result = await CreateScraper(handler).ScrapeProductsAsync("martelo", CancellationToken.None);

        Assert.Equal("20000001", Assert.Single(result.Products).ExternalId);
        var o = Assert.Single(result.Outcomes);
        Assert.Equal(OutcomeResult.Ok, o.Result);
    }

    [Fact]
    public async Task ScrapeProductsAsync_KeepsAllPricedItems()
    {
        var products = (await CreateScraper().ScrapeProductsAsync(null, CancellationToken.None)).Products;

        // Non-food items are kept; price-less items are skipped
        Assert.Equal(3, products.Count);
        Assert.Contains(products, p => p.ExternalId == "20000001");
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

    private static string Page(int numFound, IEnumerable<int> ids) =>
        "{\"numFound\":" + numFound + ",\"items\":[" + string.Join(",", ids.Select(id =>
            "{\"gridbox\":{\"data\":{\"category\":\"Food\",\"itemId\":" + id +
            ",\"fullTitle\":\"P" + id + "\",\"price\":{\"price\":1.5}}}}")) + "]}";

    [Fact]
    public async Task Lidl_AllAssortmentFixture_ParsesNonFoodItems()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lidl_search_all_p0.json"));
        var handler = new FakeHttpMessageHandler();
        // 47 raw items against a declared total of 218: the guard now walks on, so page 2 is empty.
        handler.SetupRouteSequence("q/api/search", (System.Net.HttpStatusCode.OK, json), (System.Net.HttpStatusCode.OK, "{\"items\":[]}"));

        var nonFood = 0;
        using (var doc = System.Text.Json.JsonDocument.Parse(json))
        {
            foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                var data = item.GetProperty("gridbox").GetProperty("data");
                if (data.TryGetProperty("category", out var c) && c.GetString() != "Food" &&
                    data.TryGetProperty("price", out var pr) && pr.TryGetProperty("price", out var pv) &&
                    pv.TryGetDecimal(out var price) && price > 0)
                    nonFood++;
            }
        }
        Assert.True(nonFood > 0);

        var result = await CreateScraper(handler).ScrapeProductsAsync(null, CancellationToken.None);

        Assert.True(result.Products.Count >= nonFood + 20);
        var o = Assert.Single(result.Outcomes);
        Assert.Equal(218, o.SiteTotal);
        Assert.Equal(result.Products.Count, o.ProductsListed);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Lidl_PagesToNumFound_ThreePages()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetupRoute("offset=0&fetchsize", Page(100, Enumerable.Range(1, 48)));
        handler.SetupRoute("offset=48&fetchsize", Page(100, Enumerable.Range(49, 48)));
        handler.SetupRoute("offset=96&fetchsize", Page(100, Enumerable.Range(97, 4)));

        var result = await CreateScraper(handler).ScrapeProductsAsync(null, CancellationToken.None);

        Assert.Equal(100, result.Products.Count);
        Assert.Equal(3, handler.RequestCount);
        var o = Assert.Single(result.Outcomes);
        Assert.Equal(100, o.SiteTotal);
        Assert.Equal(100, o.ProductsListed);
        Assert.Equal("(empty)", o.Key);
        Assert.Equal(OutcomeResult.Ok, o.Result);
        Assert.False(result.Incomplete);
    }

    [Fact]
    public async Task Lidl_Loop_RepeatedPage_StopsAndWarns()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetDefaultResponse(Page(500, Enumerable.Range(1, 48)));

        var result = await CreateScraper(handler).ScrapeProductsAsync(null, CancellationToken.None);

        Assert.Equal(2, handler.RequestCount);
        Assert.True(result.Incomplete);
        Assert.Contains("Loop detected in (empty) at page 2: repeated page; category ended", result.Warnings);
        Assert.Equal(48, result.Products.Count);
    }

    [Fact]
    public void Lidl_RequestCeiling_Is15()
    {
        Assert.Equal(15, CreateScraper().RequestCeiling);
    }

    [Fact]
    public async Task ScrapeStoreLocationsAsync_ReturnsEmptyList()
    {
        Assert.Empty(await CreateScraper().ScrapeStoreLocationsAsync(CancellationToken.None));
    }
}
