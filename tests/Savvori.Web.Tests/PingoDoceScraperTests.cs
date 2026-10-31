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
        var scraper = new PingoDoceScraper(factory, logger) { ReattemptDelay = TimeSpan.Zero, RequestSpacing = TimeSpan.Zero };
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

    // ---- Discovery, paging guard and request ceiling ----------------------------

    private const string IndexXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><sitemapindex xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">" +
        "<sitemap><loc>https://www.pingodoce.pt/home/sitemap_0-product.xml</loc></sitemap>" +
        "<sitemap><loc>https://www.pingodoce.pt/home/sitemap_1-product.xml</loc></sitemap>" +
        "<sitemap><loc>https://www.pingodoce.pt/home/sitemap_2.xml</loc></sitemap></sitemapindex>";

    private static string SitemapXml(params string[] paths) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">" +
        string.Concat(paths.Select(p => $"<url><loc>https://www.pingodoce.pt{p}</loc></url>")) + "</urlset>";

    private static IReadOnlyList<string> FixtureLocs() =>
        System.Xml.Linq.XDocument.Parse(LoadFixture("pingodoce-sitemap-categories.xml"))
            .Descendants().Where(e => e.Name.LocalName == "loc").Select(e => e.Value).ToList();

    private static AngleSharp.Dom.IDocument ParseHtml(string html) =>
        new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html);

    private static (PingoDoceScraper Scraper, FakeHttpMessageHandler Handler) CreateDiscoveryScraper(
        string? sitemapXml = null, string? navHtml = null)
    {
        var (scraper, handler) = CreateScraper();
        handler.SetupRoute("sitemap_index.xml", IndexXml);
        handler.SetupRoute("sitemap_2.xml", sitemapXml ?? LoadFixture("pingodoce-sitemap-categories.xml"));
        handler.SetupRoute("/home/produtos/", navHtml ?? LoadFixture("pingodoce_nav.html"));
        return (scraper, handler);
    }

    // 24 tiles in the shape of the real fixture, one per id.
    private static string Page(IEnumerable<int> ids) =>
        "<html><body>" + string.Concat(ids.Select(i =>
            $"<div class=\"product-tile-pd\" data-pid=\"{i}\" data-gtm-info='{{\"items\":[{{\"item_name\":\"P{i}\",\"price\":1.5}}]}}'></div>")) +
        "</body></html>";

    [Fact]
    public void PingoDoce_Selection_RealSitemap()
    {
        var sel = PingoDoceScraper.SelectSections(FixtureLocs());

        Assert.DoesNotContain("as-nossas-marcas", sel);
        Assert.DoesNotContain("promocoes", sel);
        Assert.DoesNotContain(sel, s => s.Contains('/'));
        Assert.Contains("limpeza", sel);
        Assert.Contains("mercearia", sel);
        Assert.Equal(sel.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), sel);
        Assert.True(sel.Count >= 25, $"count was {sel.Count}");
    }

    [Fact]
    public void PingoDoce_NavCgids_RealNav()
    {
        var map = PingoDoceScraper.ParseNavCgids(ParseHtml(LoadFixture("pingodoce_nav.html")));

        Assert.True(map.Count >= 25, $"count was {map.Count}");
        Assert.Equal("ec_limpeza_1800", map["limpeza"]);
        Assert.Equal("ec_animais_2300", map["animais"]);
        Assert.All(map.Values, v => Assert.StartsWith("ec_", v));
        Assert.DoesNotContain(map.Keys, k => k.Contains('/'));
    }

    [Fact]
    public async Task PingoDoce_Discovery_WalksAllCgids()
    {
        var (scraper, handler) = CreateDiscoveryScraper();
        // Seasonal pages are not live here; without this the generic nav route would answer their lookups.
        foreach (var slug in new[] { "natal-e-ano-novo", "pascoa", "santos-populares", "feira-do-bebe" })
            handler.SetupRouteSequence($"/home/produtos/{slug}", (HttpStatusCode.NotFound, "gone"));
        var sections = PingoDoceScraper.SelectSections(FixtureLocs());
        var cgids = PingoDoceScraper.ParseNavCgids(ParseHtml(LoadFixture("pingodoce_nav.html")));
        string[] ignored = ["freshpage", "unclassified", "valor-de-deposito"];
        string[] seasonal = ["natal-e-ano-novo", "pascoa", "santos-populares", "feira-do-bebe"];
        var withoutCgid = sections.Count(s => !cgids.ContainsKey(s));
        // Ignored sections never become categories; seasonal ones without a live page only log an Info line.
        var withCgid = sections.Count(s => cgids.ContainsKey(s) && !ignored.Contains(s));
        var warned = sections.Count(s => !cgids.ContainsKey(s) && !ignored.Contains(s) && !seasonal.Contains(s));

        var result = await scraper.ScrapeProductsAsync(ct: CancellationToken.None);

        var cats = result.Outcomes.Where(o => o.Kind == OutcomeKind.Category).ToList();
        Assert.True(cats.Count >= 25, $"categories was {cats.Count}");
        Assert.Equal(withCgid, cats.Count);
        Assert.All(cats, o => Assert.StartsWith("ec_", o.Key));
        Assert.Equal(warned, result.Warnings.Count);
        Assert.All(result.Warnings, w => Assert.StartsWith("Pingo Doce: no cgid for section '", w));
        Assert.NotEmpty(result.Products);
        Assert.DoesNotContain(result.Outcomes, o => o.Kind == OutcomeKind.Discovery);
    }

    [Fact]
    public async Task PingoDoce_MissingCgid_Warns()
    {
        var (scraper, _) = CreateDiscoveryScraper(
            SitemapXml("/home/produtos/zzz-sem-id/x", "/home/produtos/limpeza/x"));

        var result = await scraper.ScrapeProductsAsync(ct: CancellationToken.None);

        Assert.Equal("Pingo Doce: no cgid for section 'zzz-sem-id'", Assert.Single(result.Warnings));
        Assert.Contains(result.Outcomes, o => o.Kind == OutcomeKind.Category && o.Key == "ec_limpeza_1800");
    }

    private static void SetupSeasonal(FakeHttpMessageHandler handler, params string[] slugs)
    {
        // Sequences are matched before plain routes, so these win over the generic /home/produtos/ nav route.
        foreach (var slug in slugs)
            handler.SetupRouteSequence($"/home/produtos/{slug}",
                (HttpStatusCode.OK, LoadFixture($"pingodoce_seasonal_{slug}.html")));
    }

    [Fact]
    public async Task PingoDoce_SeasonalCgidResolvedFromOwnPage()
    {
        var (scraper, handler) = CreateDiscoveryScraper(
            SitemapXml("/home/produtos/natal-e-ano-novo/x", "/home/produtos/limpeza/x"));
        SetupSeasonal(handler, "natal-e-ano-novo");

        var result = await scraper.ScrapeProductsAsync(ct: CancellationToken.None);

        Assert.Contains(result.Outcomes, o => o.Kind == OutcomeKind.Category && o.Key == "ec_natal_1200000");
        Assert.DoesNotContain(result.Warnings, w => w.Contains("natal-e-ano-novo"));
    }

    [Fact]
    public async Task PingoDoce_SeasonalEmptyPage_IsContentPage()
    {
        var (scraper, handler) = CreateDiscoveryScraper(
            SitemapXml("/home/produtos/natal-e-ano-novo/x", "/home/produtos/limpeza/x"));
        SetupSeasonal(handler, "natal-e-ano-novo");
        handler.SetupRouteSequence("cgid=ec_natal_1200000", (HttpStatusCode.OK, "<html><body></body></html>"));

        var result = await scraper.ScrapeProductsAsync(ct: CancellationToken.None);

        var seasonal = Assert.Single(result.Outcomes, o => o.Key == "ec_natal_1200000");
        Assert.Equal(OutcomeResult.ContentPage, seasonal.Result);
        Assert.DoesNotContain(result.Outcomes, o => o.Result == OutcomeResult.NoTiles);
    }

    [Fact]
    public async Task PingoDoce_NonSeasonalEmptyPage_StaysNoTiles()
    {
        var (scraper, handler) = CreateDiscoveryScraper(SitemapXml("/home/produtos/limpeza/x"));
        handler.SetupRouteSequence("cgid=ec_limpeza_1800", (HttpStatusCode.OK, "<html><body></body></html>"));

        var result = await scraper.ScrapeProductsAsync(ct: CancellationToken.None);

        var o = Assert.Single(result.Outcomes, o => o.Key == "ec_limpeza_1800");
        Assert.Equal(OutcomeResult.NoTiles, o.Result);
    }

    [Fact]
    public async Task PingoDoce_IgnoredSectionsInfoNoWarning()
    {
        var (scraper, _) = CreateDiscoveryScraper(SitemapXml(
            "/home/produtos/freshpage/x", "/home/produtos/unclassified/x",
            "/home/produtos/valor-de-deposito/x", "/home/produtos/limpeza/x"));

        var result = await scraper.ScrapeProductsAsync(ct: CancellationToken.None);

        Assert.DoesNotContain(result.Warnings, w => w.Contains("no cgid"));
        var cat = Assert.Single(result.Outcomes, o => o.Kind == OutcomeKind.Category);
        Assert.Equal("ec_limpeza_1800", cat.Key);
    }

    [Fact]
    public async Task PingoDoce_24TilesWith4Unparseable_Continues()
    {
        var (scraper, handler) = CreateScraper();
        var first = Page(Enumerable.Range(1, 24));
        foreach (var id in new[] { 3, 7, 11, 15 })
            first = first.Replace($"data-pid=\"{id}\" data-gtm-info=", $"data-pid=\"{id}\" data-x=");
        handler.SetupRoute("start=0", first);
        handler.SetupRoute("start=24", Page(Enumerable.Range(25, 6)));
        handler.SetupRoute("start=48", Page([]));

        var result = await scraper.ScrapeProductsAsync("frescos", CancellationToken.None);

        // 24 raw tiles (20 parsable) do not end the category; the short second page (6 tiles) does.
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(26, result.Products.Count);
        Assert.False(result.Incomplete);
    }

    [Fact]
    public async Task PingoDoce_DiscoveryFailed_NoFallback()
    {
        var (scraper, handler) = CreateDiscoveryScraper();
        handler.SetupRoute("sitemap_index.xml", "error", HttpStatusCode.InternalServerError);

        var result = await scraper.ScrapeProductsAsync(ct: CancellationToken.None);

        var o = Assert.Single(result.Outcomes);
        Assert.Equal(OutcomeKind.Discovery, o.Kind);
        Assert.Equal(OutcomeResult.Failed, o.Result);
        Assert.Empty(result.Products);
    }

    [Fact]
    public async Task PingoDoce_DiscoveryFailed_ZeroCgids()
    {
        var (scraper, _) = CreateDiscoveryScraper(navHtml: "<html><body><a href=\"/home/produtos/limpeza\">x</a></body></html>");

        var result = await scraper.ScrapeProductsAsync(ct: CancellationToken.None);

        var o = Assert.Single(result.Outcomes);
        Assert.Equal(OutcomeKind.Discovery, o.Kind);
        Assert.Equal(OutcomeResult.Failed, o.Result);
        Assert.Empty(result.Products);
    }

    [Fact]
    public async Task PingoDoce_Loop_NoNewProducts()
    {
        var (scraper, handler) = CreateScraper();
        handler.SetupRoute("start=0", Page(Enumerable.Range(1, 24)));
        handler.SetupRoute("start=24", Page(Enumerable.Range(1, 24).Reverse()));

        var result = await scraper.ScrapeProductsAsync("frescos", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.Incomplete);
        var warning = Assert.Single(result.Warnings);
        Assert.StartsWith("Loop detected in frescos", warning);
        Assert.EndsWith("no new products; category ended", warning);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(24, result.Products.Count);
    }

    [Fact]
    public async Task PingoDoce_PagesUntilShortPage()
    {
        var (scraper, handler) = CreateScraper();
        handler.SetupRoute("start=0", Page(Enumerable.Range(1, 24)));
        handler.SetupRoute("start=24", Page(Enumerable.Range(25, 6)));

        var result = await scraper.ScrapeProductsAsync("frescos", CancellationToken.None);

        Assert.Equal(30, result.Products.Count);
        Assert.Equal(2, handler.RequestCount);
        Assert.False(result.Incomplete);
    }

    [Fact]
    public void PingoDoce_RequestCeiling_Is2000()
    {
        Assert.Equal(2000, CreateScraper().Scraper.RequestCeiling);
    }

    [Fact]
    public void StoreChainSlug_IsCorrect()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        var logger = Substitute.For<ILogger<PingoDoceScraper>>();
        var scraper = new PingoDoceScraper(factory, logger) { RequestSpacing = TimeSpan.Zero };
        Assert.Equal("pingodoce", scraper.StoreChainSlug);
    }
}
