using System.Net;
using NSubstitute;
using Savvori.WebApi.Scraping;
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
    public async Task Continente_ReportsProgressAfterEveryPage_NotOnlyWhenKeyEnds()
    {
        var (scraper, _) = CreateScraper();
        scraper.ProgressInterval = TimeSpan.Zero;
        var snaps = new List<Savvori.WebApi.Scraping.ScrapeProgress>();
        scraper.ProgressSink = snaps.Add;
        var result = await scraper.ScrapeProductsAsync("leite", CancellationToken.None);
        var o = Assert.Single(result.Outcomes);
        Assert.True(o.PagesFetched >= 1);
        var pageSnaps = snaps.Where(s => s.KeysDone == 0).ToList();
        Assert.Equal(o.PagesFetched, pageSnaps.Count);
        Assert.Equal(Enumerable.Range(1, pageSnaps.Count), pageSnaps.Select(s => s.PagesDone));
        Assert.All(pageSnaps, s => Assert.Equal("leite", s.Key));
        Assert.True(pageSnaps[0].ProductsSeen > 0);
    }

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
        handler.SetupRoute("start=36", Page(236, []));

        var result = await scraper.ScrapeProductsAsync("bebe/alimentacao", CancellationToken.None);

        var o = Assert.Single(result.Outcomes);
        Assert.Equal(236, o.SiteTotal);
        Assert.Equal(36, o.ProductsListed);
        Assert.Equal(2, handler.RequestCount);
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
        handler.SetupRoute("start=0", Page(100, Enumerable.Range(1, 35)));
        handler.SetupRoute("start=35", Page(100, Enumerable.Range(36, 35)));
        handler.SetupRoute("start=70", Page(100, Enumerable.Range(71, 30)));

        var result = await scraper.ScrapeProductsAsync("frescos", CancellationToken.None);

        Assert.Equal(3, handler.RequestUrls.Count);
        Assert.Contains("start=0&", handler.RequestUrls[0]);
        Assert.Contains("start=35&", handler.RequestUrls[1]);
        Assert.Contains("start=70&", handler.RequestUrls[2]);
        Assert.Equal(100, result.Products.Count);
        Assert.Equal(100, result.Products.Select(p => p.ExternalId).Distinct().Count());
        Assert.Equal(3, handler.RequestCount);
        Assert.False(result.Incomplete);
        var o = Assert.Single(result.Outcomes);
        Assert.Equal(100, o.SiteTotal);
        Assert.Equal(100, o.ProductsListed);
    }

    /// <summary>Counter shows <paramref name="shown"/> (the site's page step) regardless of how many tiles are served.</summary>
    private static string PageShown(int shown, int total, IEnumerable<int> ids)
        => Page(total, ids).Replace($"{ids.Count()} de {total} produtos", $"{shown} de {total} produtos");

    private static (ContinenteScraper Scraper, FakeHttpMessageHandler Handler) CreateBareScraper()
    {
        var handler = new FakeHttpMessageHandler();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("continente").Returns(new HttpClient(handler));
        var logger = Substitute.For<ILogger<ContinenteScraper>>();
        return (new ContinenteScraper(factory, logger) { ReattemptDelay = TimeSpan.Zero, RequestSpacing = TimeSpan.Zero }, handler);
    }

    private static void SetupCategories(FakeHttpMessageHandler handler, params string[] paths)
    {
        handler.SetupRoute("sitemap_index.xml",
            "<sitemapindex xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\"><sitemap><loc>https://www.continente.pt/sitemap-categories.xml</loc></sitemap></sitemapindex>");
        handler.SetupRoute("sitemap-categories.xml",
            "<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">" +
            string.Concat(paths.Select(p => $"<url><loc>https://www.continente.pt/{p}/</loc></url>")) + "</urlset>");
    }

    [Fact]
    public async Task Continente_47TilePages_AdvanceByCounterStep_FullCoverage()
    {
        var (scraper, handler) = CreateScraper();
        // Real shape: counter says 35 but the page serves 47 tiles; the next page starts at offset 35, not 47.
        handler.SetupRoute("start=0", PageShown(35, 100, Enumerable.Range(1, 47)));
        handler.SetupRoute("start=35", PageShown(70, 100, Enumerable.Range(36, 47)));
        handler.SetupRoute("start=70", PageShown(100, 100, Enumerable.Range(71, 30)));

        var result = await scraper.ScrapeProductsAsync("supermercado/congelados", CancellationToken.None);

        Assert.Equal(["0", "35", "70"],
            handler.RequestUrls.Select(u => System.Text.RegularExpressions.Regex.Match(u, @"start=(\d+)").Groups[1].Value));
        Assert.Equal(100, result.Products.Select(p => p.ExternalId).Distinct().Count());
        Assert.Empty(result.Warnings);
        var o = Assert.Single(result.Outcomes);
        Assert.StartsWith("reached declared total", o.StopReason);
    }

    [Fact]
    public async Task Continente_CatalogueWideCategory_StopsEarly_LaterCategoriesStillScraped()
    {
        var (scraper, handler) = CreateBareScraper();
        SetupCategories(handler, "supermercado/a", "supermercado/wide", "zeta/x");
        // a: 700 distinct products in 20 pages of 35.
        for (var k = 0; k < 20; k++)
            handler.SetupRoute($"supermercado/a/?start={k * 35}&", Page(700, Enumerable.Range(1 + k * 35, 35)));
        // wide: declares a huge total and serves pages of products the run already has (new only to its own guard).
        for (var k = 0; k < 20; k++)
            handler.SetupRoute($"supermercado/wide/?start={k * 35}&", Page(69358, Enumerable.Range(1 + k * 35, 35)));
        handler.SetupRoute("zeta/x/?start=0&", Page(35, Enumerable.Range(5000, 35)));

        var result = await scraper.ScrapeProductsAsync(null, CancellationToken.None);

        var a = result.Outcomes.Single(o => o.Key == "supermercado/a");
        var wide = result.Outcomes.Single(o => o.Key == "supermercado/wide");
        var zeta = result.Outcomes.Single(o => o.Key == "zeta/x");
        Assert.Equal(700, a.ProductsFound);
        Assert.Equal(0, wide.ProductsFound);
        Assert.Equal(10, wide.PagesFetched);
        Assert.StartsWith("no new products for 10 consecutive pages", wide.StopReason);
        Assert.Equal(35, zeta.ProductsFound);
        Assert.Equal(735, result.Products.Count);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Continente_NormalCategoryOverlappingEarlierOne_NotStoppedEarly()
    {
        var (scraper, handler) = CreateBareScraper();
        SetupCategories(handler, "supermercado/a", "supermercado/b");
        for (var k = 0; k < 4; k++)
            handler.SetupRoute($"supermercado/a/?start={k * 35}&", Page(140, Enumerable.Range(1 + k * 35, 35)));
        // b: first two pages are all already seen (below the stagnation limit), then new products appear.
        handler.SetupRoute("supermercado/b/?start=0&", Page(105, Enumerable.Range(1, 35)));
        handler.SetupRoute("supermercado/b/?start=35&", Page(105, Enumerable.Range(36, 35)));
        handler.SetupRoute("supermercado/b/?start=70&", Page(105, Enumerable.Range(141, 35)));

        var result = await scraper.ScrapeProductsAsync(null, CancellationToken.None);

        var b = result.Outcomes.Single(o => o.Key == "supermercado/b");
        Assert.Equal(3, b.PagesFetched);
        Assert.Equal(35, b.ProductsFound);
        Assert.StartsWith("reached declared total", b.StopReason);
        Assert.Equal(175, result.Products.Count);
    }

    [Fact]
    public async Task Continente_ServedPastTotal_NaturalEnd()
    {
        var (scraper, handler) = CreateScraper();
        handler.SetupRoute("start=0", Page(40, Enumerable.Range(1, 35)));
        handler.SetupRoute("start=35", Page(40, Enumerable.Range(36, 35)));

        var result = await scraper.ScrapeProductsAsync("frescos", CancellationToken.None);

        Assert.Equal(2, handler.RequestCount);
        Assert.False(result.Incomplete);
        Assert.Empty(result.Warnings);
        Assert.StartsWith("reached declared total", Assert.Single(result.Outcomes).StopReason);
    }

    [Fact]
    public void Continente_DestaquesExcluded()
    {
        var selected = ContinenteScraper.SelectCategories(["/destaques/ofertas/", "/marcas/x/", "/mercearia/arroz/"]);

        Assert.DoesNotContain(selected, s => s.StartsWith("destaques") || s.StartsWith("marcas"));
        Assert.Contains(selected, s => s.StartsWith("mercearia"));
    }

    [Fact]
    public async Task Continente_CounterlessEmptyPage_ContentPage()
    {
        var (scraper, handler) = CreateScraper();
        handler.SetupRoute("start=0", Page(null, []));

        var result = await scraper.ScrapeProductsAsync("frescos", CancellationToken.None);

        var o = Assert.Single(result.Outcomes);
        Assert.Equal(OutcomeResult.ContentPage, o.Result);
        Assert.Equal(1, handler.RequestCount);
        Assert.StartsWith("content page", o.StopReason);
        Assert.False(result.Incomplete);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Continente_LandingPageRepeat_NaturalEndNotIncomplete()
    {
        var (scraper, handler) = CreateScraper();
        handler.SetupRoute("start=0", Page(null, Enumerable.Range(1, 5)));
        handler.SetupRoute("start=5", Page(null, Enumerable.Range(1, 5)));

        var result = await scraper.ScrapeProductsAsync("frescos", CancellationToken.None);

        Assert.Equal(2, handler.RequestCount);
        Assert.False(result.Incomplete);
        Assert.Empty(result.Warnings);
        Assert.StartsWith("landing page", Assert.Single(result.Outcomes).StopReason);
        Assert.Equal(5, result.Products.Count);
    }

    [Fact]
    public async Task Continente_CountedRepeat_LoopIncomplete()
    {
        var (scraper, handler) = CreateScraper();
        handler.SetupRoute("start=0", Page(500, Enumerable.Range(1, 35)));
        handler.SetupRoute("start=35", Page(500, Enumerable.Range(1, 35)));

        var result = await scraper.ScrapeProductsAsync("frescos", CancellationToken.None);

        Assert.True(result.Incomplete);
        Assert.StartsWith("Loop detected in ", Assert.Single(result.Warnings));
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Continente_CounterlessLargeListing_NotShortPage()
    {
        var (scraper, handler) = CreateScraper();
        handler.SetupRoute("start=0", LoadFixture("continente_livros.html"));

        var result = await scraper.ScrapeProductsAsync("livros", CancellationToken.None);

        var o = Assert.Single(result.Outcomes);
        Assert.Equal(93, o.ProductsListed);
        Assert.Equal(2, handler.RequestCount);
        Assert.DoesNotContain(result.Warnings, w => w.StartsWith("Loop detected"));
        Assert.StartsWith("empty page", o.StopReason);
    }

    [Fact]
    public async Task Continente_MerceariaFixtures_StepIsServedTiles()
    {
        var (scraper, handler) = CreateScraper();
        handler.SetupRoute("start=0", LoadFixture("continente_mercearia_s0.html"));
        handler.SetupRoute("start=35", LoadFixture("continente_mercearia_s35.html"));

        var result = await scraper.ScrapeProductsAsync("mercearia", CancellationToken.None);

        Assert.Contains("start=35&", handler.RequestUrls[1]);
        Assert.Contains("start=70&", handler.RequestUrls[2]);
        var o = Assert.Single(result.Outcomes);
        Assert.Equal(5447, o.SiteTotal);
        Assert.Equal(70, o.ProductsListed);
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
    public void Continente_RequestCeiling_Is8000()
    {
        Assert.Equal(8000, CreateScraper().Scraper.RequestCeiling);
    }

    [Fact]
    public void Continente_WallClockLimit_Is8Hours()
    {
        Assert.Equal(TimeSpan.FromHours(8), CreateScraper().Scraper.WallClockLimit);
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

    private static string Tile(string impressionJson) =>
        $"<div class=\"product-tile\" data-product-tile-impression='{impressionJson}'></div>";

    private static async Task<ScrapeRunResult> ScrapeTiles(params string[] tiles)
    {
        var (scraper, handler) = CreateScraper();
        handler.SetupRoute("start=0", "<html><body>" + string.Concat(tiles) + "</body></html>");
        return await scraper.ScrapeProductsAsync("frescos", CancellationToken.None);
    }

    [Fact]
    public async Task Continente_NullPriceTile_SkippedWithoutThrowing()
    {
        var result = await ScrapeTiles(
            Tile("{\"name\":\"Sem Preco\",\"id\":\"1\",\"price\":null,\"brand\":\"X\",\"category\":\"A/B\"}"),
            Tile("{\"name\":\"Com Preco\",\"id\":\"2\",\"price\":1.5,\"brand\":\"X\",\"category\":\"A/B\"}"));
        var p = Assert.Single(result.Products);
        Assert.Equal("2", p.ExternalId);
    }

    [Fact]
    public async Task Continente_NumericId_Works()
    {
        var result = await ScrapeTiles(
            Tile("{\"name\":\"Numerico\",\"id\":12345,\"price\":2.25,\"brand\":null,\"category\":\"A/B\"}"));
        var p = Assert.Single(result.Products);
        Assert.Equal("12345", p.ExternalId);
        Assert.Null(p.Brand);
    }

    [Fact]
    public async Task Continente_MalformedTile_DoesNotDropGoodTiles()
    {
        var result = await ScrapeTiles(
            Tile("{\"name\":\"Bom 1\",\"id\":\"10\",\"price\":1.0,\"category\":\"A/B\"}"),
            Tile("{\"name\":{\"x\":1},\"id\":[1],\"price\":\"abc\",\"category\":5}"),
            Tile("{\"name\":\"Bom 2\",\"id\":\"11\",\"price\":2.0,\"category\":\"A/B\"}"));
        Assert.Equal(new[] { "10", "11" }, result.Products.Select(x => x.ExternalId).OrderBy(x => x));
    }
}
