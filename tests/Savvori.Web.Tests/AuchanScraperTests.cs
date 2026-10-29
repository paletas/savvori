using System.Net;
using NSubstitute;
using Savvori.WebApi.Scraping.Scrapers;
using Savvori.Shared;
using Microsoft.Extensions.Logging;

namespace Savvori.Web.Tests;

public class AuchanScraperTests
{
    private static string LoadFixture(string filename)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", filename);
        return File.ReadAllText(path);
    }

    private static (AuchanScraper Scraper, FakeHttpMessageHandler Handler) CreateScraper()
    {
        var html = LoadFixture("auchan_products.html");
        var handler = new FakeHttpMessageHandler();
        // start=0 returns products, start=24 returns empty (stops pagination)
        handler.SetupRoute("start=0", html);
        handler.SetupRoute("start=24", "<html><body></body></html>");

        var httpClient = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("auchan").Returns(httpClient);

        var logger = Substitute.For<ILogger<AuchanScraper>>();
        return (new AuchanScraper(factory, logger) { ReattemptDelay = TimeSpan.Zero, RequestSpacing = TimeSpan.Zero }, handler);
    }

    [Fact]
    public async Task Auchan_Probe_Ok()
    {
        var (scraper, handler) = CreateScraper();
        var r = await scraper.ProbeAsync(CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Auchan_Probe_410_NotOk()
    {
        var (scraper, handler) = CreateScraper();
        handler.SetupRoute("start=0", "gone", HttpStatusCode.Gone);
        var r = await scraper.ProbeAsync(CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Auchan_410OnFirstPage_OutcomeFailed()
    {
        var (scraper, handler) = CreateScraper();
        handler.SetupRoute("start=0", "gone", HttpStatusCode.Gone);

        var result = await scraper.ScrapeProductsAsync("leite", CancellationToken.None);

        Assert.Empty(result.Products);
        var o = Assert.Single(result.Outcomes);
        Assert.Equal(OutcomeKind.Term, o.Kind);
        Assert.Equal("leite", o.Key);
        Assert.Equal(OutcomeResult.Failed, o.Result);
        Assert.True(o.StatusCounts.ContainsKey(410));
    }

    [Fact]
    public async Task Auchan_HealthyFirstPage_OutcomeOk()
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
        var products = (await scraper.ScrapeProductsAsync("leite-e-laticínios", CancellationToken.None)).Products;

        // 4 valid products; 2 invalid (no pid, price=0) are skipped
        Assert.Equal(4, products.Count);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesNameAndBrand()
    {
        var (scraper, _) = CreateScraper();
        var products = (await scraper.ScrapeProductsAsync("leite-e-laticínios", CancellationToken.None)).Products;

        var leite = products.First(p => p.ExternalId == "3010403");
        Assert.Equal("LEITE AUCHAN UHT MEIO GORDO SLIM 1L", leite.Name);
        Assert.Equal("AUCHAN", leite.Brand);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesPrice()
    {
        var (scraper, _) = CreateScraper();
        var products = (await scraper.ScrapeProductsAsync("leite-e-laticínios", CancellationToken.None)).Products;

        var leite = products.First(p => p.ExternalId == "3010403");
        Assert.Equal(0.86m, leite.Price);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesUnitPrice()
    {
        var (scraper, _) = CreateScraper();
        var products = (await scraper.ScrapeProductsAsync("leite-e-laticínios", CancellationToken.None)).Products;

        var leite = products.First(p => p.ExternalId == "3010403");
        Assert.Equal(0.86m, leite.UnitPrice);
    }

    [Fact]
    public async Task ScrapeProductsAsync_DetectsPromotion_WhenDimension7IsYes()
    {
        var (scraper, _) = CreateScraper();
        var products = (await scraper.ScrapeProductsAsync("leite-e-laticínios", CancellationToken.None)).Products;

        var mimosa = products.First(p => p.ExternalId == "11885");
        Assert.True(mimosa.IsPromotion);
    }

    [Fact]
    public async Task ScrapeProductsAsync_DetectsPromotion_WhenPromoElementPresent()
    {
        // The Mimosa product has auc-price__promotion element even if dimension7 weren't "Yes"
        var (scraper, _) = CreateScraper();
        var products = (await scraper.ScrapeProductsAsync("leite-e-laticínios", CancellationToken.None)).Products;

        var mimosa = products.First(p => p.ExternalId == "11885");
        Assert.True(mimosa.IsPromotion);
    }

    [Fact]
    public async Task ScrapeProductsAsync_NoPromotion_WhenDimension7IsNo()
    {
        var (scraper, _) = CreateScraper();
        var products = (await scraper.ScrapeProductsAsync("leite-e-laticínios", CancellationToken.None)).Products;

        var leite = products.First(p => p.ExternalId == "3010403");
        Assert.False(leite.IsPromotion);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ExtractsCategory_AsLeafOfPath()
    {
        var (scraper, _) = CreateScraper();
        var products = (await scraper.ScrapeProductsAsync("leite-e-laticínios", CancellationToken.None)).Products;

        var leite = products.First(p => p.ExternalId == "3010403");
        Assert.Equal("leite-uht", leite.Category);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ExtractsSourceUrl()
    {
        var (scraper, _) = CreateScraper();
        var products = (await scraper.ScrapeProductsAsync("leite-e-laticínios", CancellationToken.None)).Products;

        var leite = products.First(p => p.ExternalId == "3010403");
        Assert.NotNull(leite.SourceUrl);
        Assert.Contains("auchan.pt", leite.SourceUrl);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ExtractsSizeFromProductName()
    {
        var (scraper, _) = CreateScraper();
        var products = (await scraper.ScrapeProductsAsync("leite-e-laticínios", CancellationToken.None)).Products;

        var leite = products.First(p => p.ExternalId == "3010403");
        Assert.Equal(1m, leite.SizeValue);
        Assert.Equal(ProductUnit.L, leite.Unit);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ExtractsMultipackSize()
    {
        var (scraper, _) = CreateScraper();
        var products = (await scraper.ScrapeProductsAsync("leite-e-laticínios", CancellationToken.None)).Products;

        var multipack = products.First(p => p.ExternalId == "3010402");
        Assert.Equal("LEITE AUCHAN UHT MEIO GORDO SLIM 6X1L", multipack.Name);
        Assert.Equal(5.16m, multipack.Price);
    }

    [Fact]
    public async Task ScrapeProductsAsync_DeduplicatesById()
    {
        var html = LoadFixture("auchan_products.html");
        // Replace first occurrence of pid 11885 with pid 3010403 to create a duplicate
        const string search = "data-pid=\"11885\"";
        var idx = html.IndexOf(search, StringComparison.Ordinal);
        var htmlWithDuplicate = idx >= 0
            ? string.Concat(html.AsSpan(0, idx), "data-pid=\"3010403\"", html.AsSpan(idx + search.Length))
            : html;

        var handler = new FakeHttpMessageHandler();
        handler.SetupRoute("start=0", htmlWithDuplicate);
        handler.SetupRoute("start=24", "<html><body></body></html>");

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("auchan").Returns(new HttpClient(handler));
        var logger = Substitute.For<ILogger<AuchanScraper>>();
        var scraper = new AuchanScraper(factory, logger) { RequestSpacing = TimeSpan.Zero };

        var products = (await scraper.ScrapeProductsAsync("leite-e-laticínios", CancellationToken.None)).Products;

        var leiteCount = products.Count(p => p.ExternalId == "3010403");
        Assert.Equal(1, leiteCount);
    }

    [Fact]
    public async Task ScrapeProductsAsync_SkipsProductsWithNoPid()
    {
        var (scraper, _) = CreateScraper();
        var products = (await scraper.ScrapeProductsAsync("leite-e-laticínios", CancellationToken.None)).Products;

        Assert.DoesNotContain(products, p => p.Name == "PRODUTO SEM ID");
    }

    [Fact]
    public async Task ScrapeProductsAsync_SkipsProductsWithZeroPrice()
    {
        var (scraper, _) = CreateScraper();
        var products = (await scraper.ScrapeProductsAsync("leite-e-laticínios", CancellationToken.None)).Products;

        Assert.DoesNotContain(products, p => p.Name == "PRODUTO SEM PRECO");
    }

    [Fact]
    public async Task ScrapeStoreLocationsAsync_ReturnsEmptyList()
    {
        var (scraper, _) = CreateScraper();
        var locations = await scraper.ScrapeStoreLocationsAsync(CancellationToken.None);
        Assert.Empty(locations);
    }

    private static string Page(int total, IEnumerable<int> ids)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<html><body><div class=\"auc-search-results auc-js-search-results-count\">1 - 24 de ")
          .Append(total.ToString("N0", System.Globalization.CultureInfo.InvariantCulture))
          .Append(" resultados</div>");
        foreach (var id in ids)
            sb.Append($"<div class=\"product-tile\" data-pid=\"{id}\" data-gtm='{{\"name\":\"P{id}\",\"price\":\"1.50\"}}'></div>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static (AuchanScraper Scraper, FakeHttpMessageHandler Handler) CreateWith(Action<FakeHttpMessageHandler> setup)
    {
        var handler = new FakeHttpMessageHandler();
        setup(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("auchan").Returns(new HttpClient(handler));
        return (new AuchanScraper(factory, Substitute.For<ILogger<AuchanScraper>>()) { ReattemptDelay = TimeSpan.Zero, RequestSpacing = TimeSpan.Zero }, handler);
    }

    [Fact]
    public async Task Auchan_Loop_OffsetIgnored_StopsAndWarns()
    {
        var (scraper, handler) = CreateWith(h => h.SetDefaultResponse(Page(500, Enumerable.Range(1, 24))));

        var result = await scraper.ScrapeProductsAsync(null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, handler.RequestCount);
        Assert.True(result.Incomplete);
        Assert.Equal("Loop detected in root at page 2: repeated page; category ended", Assert.Single(result.Warnings));
        Assert.Equal(24, result.Products.Count);
    }

    [Fact]
    public async Task Auchan_SiteTotal_FromRealCountFixture()
    {
        var (scraper, handler) = CreateWith(h => h.SetDefaultResponse(LoadFixture("auchan_root_count.html")));

        var result = await scraper.ScrapeProductsAsync(null, CancellationToken.None);

        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal("root", outcome.Key);
        Assert.Equal(57562, outcome.SiteTotal);
        Assert.Equal(OutcomeResult.NoTiles, outcome.Result);
        Assert.Equal(2, handler.RequestCount); // page 1 plus the end-of-run reattempt of a non-Ok outcome
    }

    [Fact]
    public async Task Auchan_LiveFixture_TotalAndTiles()
    {
        var (scraper, _) = CreateWith(h =>
        {
            h.SetupRoute("start=0", LoadFixture("auchan_root_p2.html"));
            h.SetupRoute("start=24", "<html><body></body></html>");
        });

        var result = await scraper.ScrapeProductsAsync(null, CancellationToken.None);

        var outcome = Assert.Single(result.Outcomes);
        Assert.NotNull(outcome.SiteTotal);
        Assert.InRange(outcome.SiteTotal!.Value, 54684, 60440);
        Assert.True(result.Products.Count >= 20);
        Assert.All(result.Products, p =>
        {
            Assert.True(p.Price > 0);
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
        });
    }

    [Fact]
    public async Task Auchan_PagesToTotal_ThreePages()
    {
        var (scraper, handler) = CreateWith(h =>
        {
            h.SetupRoute("cgid=root&start=0", Page(60, Enumerable.Range(1, 24)));
            h.SetupRoute("start=24", Page(60, Enumerable.Range(25, 24)));
            h.SetupRoute("start=48", Page(60, Enumerable.Range(49, 12)));
        });

        var result = await scraper.ScrapeProductsAsync(null, CancellationToken.None);

        Assert.Equal(60, result.Products.Count);
        Assert.Equal(3, handler.RequestCount);
        Assert.False(result.Incomplete);
        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal("root", outcome.Key);
        Assert.Equal(60, outcome.SiteTotal);
        Assert.Equal(60, outcome.ProductsListed);
        Assert.Equal(OutcomeResult.Ok, outcome.Result);
    }

    [Fact]
    public async Task Auchan_23Of24ParsableMidList_Continues()
    {
        var (scraper, handler) = CreateWith(h =>
        {
            h.SetupRoute("cgid=root&start=0", Page(60, Enumerable.Range(1, 24)));
            h.SetupRoute("start=24", Page(60, Enumerable.Range(25, 23))
                .Replace("</body>", "<div class=\"product-tile\"></div></body>"));
            h.SetupRoute("start=48", Page(60, Enumerable.Range(49, 12)));
        });

        var result = await scraper.ScrapeProductsAsync(null, CancellationToken.None);

        Assert.Equal(3, handler.RequestCount);
        Assert.Equal(59, result.Products.Count);
        Assert.False(result.Incomplete);
    }

    [Fact]
    public async Task Auchan_RootOutcome_StopReasonReachedDeclaredTotal()
    {
        var (scraper, _) = CreateWith(h =>
        {
            h.SetupRoute("cgid=root&start=0", Page(60, Enumerable.Range(1, 24)));
            h.SetupRoute("start=24", Page(60, Enumerable.Range(25, 24)));
            h.SetupRoute("start=48", Page(60, Enumerable.Range(49, 12)));
        });

        var result = await scraper.ScrapeProductsAsync(null, CancellationToken.None);

        var outcome = Assert.Single(result.Outcomes);
        Assert.NotNull(outcome.StopReason);
        Assert.StartsWith("reached declared total", outcome.StopReason);
    }

    [Fact]
    public async Task Auchan_PastDeclaredTotal_Loop()
    {
        var (scraper, _) = CreateWith(h =>
        {
            // Declared 20, 18 served per page: the second request (start=24) lands past the total.
            h.SetupRoute("start=0", Page(20, Enumerable.Range(1, 18)));
            h.SetupRoute("start=24", Page(20, Enumerable.Range(25, 18)));
        });

        var result = await scraper.ScrapeProductsAsync(null, CancellationToken.None);

        Assert.True(result.Incomplete);
        var warning = Assert.Single(result.Warnings);
        Assert.StartsWith("Loop detected in root at page", warning);
        Assert.Contains("past declared total", warning);
    }

    [Fact]
    public void Auchan_RequestCeiling_Is7200()
    {
        Assert.Equal(7200, CreateScraper().Scraper.RequestCeiling);
    }

    [Fact]
    public async Task Auchan_NoSearchTermsOrSortParams()
    {
        var (scraper, handler) = CreateScraper();

        await scraper.ScrapeProductsAsync(null, CancellationToken.None);

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ScrapeProductsAsync_StoreChainSlugIsAuchan()
    {
        var scraper = CreateScraper().Scraper;
        Assert.Equal("auchan", scraper.StoreChainSlug);
    }
}
