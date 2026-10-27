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
        var scraper = new ContinenteScraper(factory, logger) { ReattemptDelay = TimeSpan.Zero, RequestSpacing = TimeSpan.Zero };
        return (scraper, handler);
    }

    private const string SitemapIndexXml =
        "<sitemapindex xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\"><sitemap><loc>https://www.continente.pt/sitemap-categories.xml</loc></sitemap></sitemapindex>";

    [Fact]
    public async Task Continente_Probe_Ok()
    {
        var (scraper, handler) = CreateScraper();
        handler.SetupRoute("sitemap_index.xml", SitemapIndexXml);
        var r = await scraper.ProbeAsync(CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Continente_Probe_SitemapGone_NotOk_OneRequest()
    {
        var (scraper, handler) = CreateScraper();
        handler.SetupRoute("sitemap_index.xml", "gone", HttpStatusCode.Gone);
        var r = await scraper.ProbeAsync(CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Continente_Probe_CategoryNoTiles_NotOk()
    {
        var (scraper, handler) = CreateScraper();
        handler.SetupRoute("sitemap_index.xml", SitemapIndexXml);
        handler.SetupRoute("start=0", "<html><body></body></html>");
        var r = await scraper.ProbeAsync(CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Continente_FailedCategory_RecoveredAtEnd()
    {
        var (scraper, handler) = CreateScraper();
        handler.SetupRouteSequence("start=0",
            (HttpStatusCode.Gone, "gone"), (HttpStatusCode.OK, LoadFixture("continente_products.html")));
        var result = await scraper.ScrapeProductsAsync("frescos", CancellationToken.None);
        var o = Assert.Single(result.Outcomes);
        Assert.Equal(OutcomeResult.Recovered, o.Result);
        Assert.NotEmpty(result.Products);
    }

    [Fact]
    public async Task Continente_410OnFirstPage_OutcomeFailed()
    {
        var (scraper, handler) = CreateScraper();
        handler.SetupRoute("start=0", "gone", HttpStatusCode.Gone);

        var result = await scraper.ScrapeProductsAsync("frescos", CancellationToken.None);

        Assert.Empty(result.Products);
        var o = Assert.Single(result.Outcomes);
        Assert.Equal(OutcomeKind.Category, o.Kind);
        Assert.Equal(OutcomeResult.Failed, o.Result);
        Assert.True(o.StatusCounts.ContainsKey(410));
    }

    [Fact]
    public async Task Continente_HealthyFirstPage_OutcomeOk()
    {
        var (scraper, _) = CreateScraper();

        var result = await scraper.ScrapeProductsAsync("frescos", CancellationToken.None);

        var o = Assert.Single(result.Outcomes);
        Assert.Equal(OutcomeResult.Ok, o.Result);
        Assert.True(o.TilesSeen > 0);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesProductsFromFixture()
    {
        var (scraper, _) = CreateScraper();

        var products = (await scraper.ScrapeProductsAsync("frescos", CancellationToken.None)).Products;

        // 3 valid products (1 with price=0 is skipped)
        Assert.Equal(3, products.Count);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesNameAndBrand()
    {
        var (scraper, _) = CreateScraper();
        var products = (await scraper.ScrapeProductsAsync("frescos", CancellationToken.None)).Products;

        var leite = products.First(p => p.ExternalId == "8696608");
        Assert.Equal("Leite UHT Magro sem Lactose Continente", leite.Name);
        Assert.Equal("Continente", leite.Brand);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesPrice()
    {
        var (scraper, _) = CreateScraper();
        var products = (await scraper.ScrapeProductsAsync("frescos", CancellationToken.None)).Products;

        var leite = products.First(p => p.ExternalId == "8696608");
        Assert.Equal(1.08m, leite.Price);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesUnitPrice()
    {
        var (scraper, _) = CreateScraper();
        var products = (await scraper.ScrapeProductsAsync("frescos", CancellationToken.None)).Products;

        var leite = products.First(p => p.ExternalId == "8696608");
        Assert.Equal(1.08m, leite.UnitPrice);
        Assert.Equal(ProductUnit.L, leite.Unit);
        Assert.Equal(1.0m, leite.SizeValue);
    }

    [Fact]
    public async Task ScrapeProductsAsync_DetectsPromotion()
    {
        var (scraper, _) = CreateScraper();
        var products = (await scraper.ScrapeProductsAsync("frescos", CancellationToken.None)).Products;

        var mimosa = products.First(p => p.ExternalId == "2210947");
        Assert.True(mimosa.IsPromotion);
        Assert.Equal(0.9m, mimosa.Price);
    }

    [Fact]
    public async Task ScrapeProductsAsync_NonPromoProduct_IsNotPromotion()
    {
        var (scraper, _) = CreateScraper();
        var products = (await scraper.ScrapeProductsAsync("frescos", CancellationToken.None)).Products;

        var leite = products.First(p => p.ExternalId == "8696608");
        Assert.False(leite.IsPromotion);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesCategory()
    {
        var (scraper, _) = CreateScraper();
        var products = (await scraper.ScrapeProductsAsync("frescos", CancellationToken.None)).Products;

        var leite = products.First(p => p.ExternalId == "8696608");
        Assert.Equal("Leite sem Lactose", leite.Category);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesImageUrl()
    {
        var (scraper, _) = CreateScraper();
        var products = (await scraper.ScrapeProductsAsync("frescos", CancellationToken.None)).Products;

        var leite = products.First(p => p.ExternalId == "8696608");
        Assert.NotNull(leite.ImageUrl);
        Assert.Contains("8696608", leite.ImageUrl);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesSourceUrl()
    {
        var (scraper, _) = CreateScraper();
        var products = (await scraper.ScrapeProductsAsync("frescos", CancellationToken.None)).Products;

        var leite = products.First(p => p.ExternalId == "8696608");
        Assert.NotNull(leite.SourceUrl);
        Assert.Contains("8696608", leite.SourceUrl);
    }

    [Fact]
    public async Task ScrapeProductsAsync_SkipsProductsWithZeroPrice()
    {
        var (scraper, _) = CreateScraper();
        var products = (await scraper.ScrapeProductsAsync("frescos", CancellationToken.None)).Products;

        Assert.DoesNotContain(products, p => p.ExternalId == "9999999");
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesGramProduct()
    {
        var (scraper, _) = CreateScraper();
        var products = (await scraper.ScrapeProductsAsync("frescos", CancellationToken.None)).Products;

        var queijo = products.First(p => p.ExternalId == "1234567");
        Assert.Equal(ProductUnit.G, queijo.Unit);
        Assert.Equal(500m, queijo.SizeValue);
        Assert.Equal(4.98m, queijo.UnitPrice);
    }

    private static string Page(int? total, IEnumerable<int> ids)
    {
        var list = ids.ToList();
        var counter = total is null ? "" : $"<div class=\"search-results-products-counter\">{list.Count} de {total} produtos</div>";
        var tiles = string.Concat(list.Select(id =>
            "<div class=\"product-tile\" data-product-tile-impression=\"" +
            WebUtility.HtmlEncode("{\"id\":\"" + id + "\",\"name\":\"P" + id + "\",\"price\":1.5}") +
            "\"></div>"));
        return "<html><body>" + counter + tiles + "</body></html>";
    }

    [Fact]
    public async Task Continente_SiteTotal_FromCounter()
    {
        var (scraper, handler) = CreateScraper();
        handler.SetupRoute("start=0", Page(236, Enumerable.Range(1, 36)));

        var result = await scraper.ScrapeProductsAsync("bebe/alimentacao", CancellationToken.None);

        var o = Assert.Single(result.Outcomes);
        Assert.Equal(236, o.SiteTotal);
        Assert.Equal(36, o.ProductsListed);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Continente_LiveFixture_TotalAndTiles()
    {
        var (scraper, handler) = CreateScraper();
        handler.SetupRoute("start=0", LoadFixture("continente_category_live.html"));

        var result = await scraper.ScrapeProductsAsync("bebe/alimentacao", CancellationToken.None);

        var o = Assert.Single(result.Outcomes);
        Assert.NotNull(o.SiteTotal);
        Assert.InRange(o.SiteTotal!.Value, 213, 259);
        Assert.True(result.Products.Count >= 3);
        Assert.All(result.Products, p =>
        {
            Assert.True(p.Price > 0);
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
        });
    }

    [Fact]
    public async Task Continente_SiteTotal_FallsBackToProductCount()
    {
        var (scraper, _) = CreateScraper();

        var result = await scraper.ScrapeProductsAsync("frescos", CancellationToken.None);

        Assert.Equal(4, Assert.Single(result.Outcomes).SiteTotal);
    }

    [Fact]
    public async Task Continente_NoTotal_SiteTotalNull()
    {
        var (scraper, handler) = CreateScraper();
        handler.SetupRoute("start=0", Page(null, Enumerable.Range(1, 10)));

        var result = await scraper.ScrapeProductsAsync("frescos", CancellationToken.None);

        var o = Assert.Single(result.Outcomes);
        Assert.Null(o.SiteTotal);
        Assert.Equal(OutcomeResult.Ok, o.Result);
    }

    [Fact]
    public async Task Continente_PagesToTotal()
    {
        var (scraper, handler) = CreateScraper();
        handler.SetupRoute("start=0", Page(100, Enumerable.Range(1, 48)));
        handler.SetupRoute("start=48", Page(100, Enumerable.Range(49, 48)));
        handler.SetupRoute("start=96", Page(100, Enumerable.Range(97, 4)));

        var result = await scraper.ScrapeProductsAsync("frescos", CancellationToken.None);

        Assert.Equal(100, result.Products.Count);
        Assert.Equal(3, handler.RequestCount);
        Assert.False(result.Incomplete);
        var o = Assert.Single(result.Outcomes);
        Assert.Equal(100, o.SiteTotal);
        Assert.Equal(100, o.ProductsListed);
    }

    [Fact]
    public async Task Continente_PastDeclaredTotal_Loop()
    {
        var (scraper, handler) = CreateScraper();
        handler.SetupRoute("start=0", Page(10, Enumerable.Range(1, 48)));
        handler.SetupRoute("start=48", Page(10, Enumerable.Range(49, 48)));

        var result = await scraper.ScrapeProductsAsync("frescos", CancellationToken.None);

        Assert.True(result.Incomplete);
        var warning = Assert.Single(result.Warnings);
        Assert.StartsWith("Loop detected in ", warning);
        Assert.Contains("past declared total", warning);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Continente_Loop_RepeatedPage_StopsAndWarns()
    {
        var (scraper, handler) = CreateScraper();
        handler.SetupRoute("start=0", Page(500, Enumerable.Range(1, 48)));
        handler.SetupRoute("start=48", Page(500, Enumerable.Range(1, 48)));

        var result = await scraper.ScrapeProductsAsync("frescos", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, handler.RequestCount);
        Assert.True(result.Incomplete);
        Assert.Equal("Loop detected in frescos at page 2: repeated page; category ended", Assert.Single(result.Warnings));
        Assert.Equal(48, result.Products.Count);
    }

    [Fact]
    public void Continente_RequestCeiling_Is3800()
    {
        Assert.Equal(3800, CreateScraper().Scraper.RequestCeiling);
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
        var scraper = new ContinenteScraper(factory, logger) { RequestSpacing = TimeSpan.Zero };
        Assert.Equal("continente", scraper.StoreChainSlug);
    }
}
