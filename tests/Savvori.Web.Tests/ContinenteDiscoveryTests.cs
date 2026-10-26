using System.Net;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Savvori.Shared;
using Savvori.WebApi.Scraping;
using Savvori.WebApi.Scraping.Scrapers;

namespace Savvori.Web.Tests;

public class ContinenteDiscoveryTests
{
    private const string LeafPath = "supermercado/frutas-e-legumes/frutas/banana";

    private static string LoadFixture(string filename)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", filename));

    private static (ContinenteScraper Scraper, FakeHttpMessageHandler Handler) CreateScraper()
    {
        var handler = new FakeHttpMessageHandler();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("continente").Returns(new HttpClient(handler));
        var logger = Substitute.For<ILogger<ContinenteScraper>>();
        return (new ContinenteScraper(factory, logger) { ReattemptDelay = TimeSpan.Zero }, handler);
    }

    private static string Urlset(params string[] paths)
        => "<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">" +
           string.Concat(paths.Select(p => $"<url><loc>https://www.continente.pt{p}</loc></url>")) +
           "</urlset>";

    /// <summary>
    /// The real category sitemap has ~780 leaves (one 300 ms delay each), so the test keeps the first
    /// few URLs of each top level (and one of every other top level); the shape is the real one.
    /// </summary>
    private static string TrimmedCategorySitemap()
    {
        var doc = XDocument.Parse(LoadFixture("continente-sitemap-categories.xml"));
        var ns = doc.Root!.GetDefaultNamespace();
        var kept = doc.Descendants(ns + "url")
            .GroupBy(u => new Uri(u.Element(ns + "loc")!.Value).AbsolutePath.Trim('/').Split('/')[0])
            .SelectMany(g => g.Key is "supermercado" or "bebe" or "animais" or "beleza" or "marcas"
                ? g.Take(5) : g.Take(1));
        return new XDocument(new XElement(ns + "urlset", kept)).ToString();
    }

    [Fact]
    public async Task Sitemap_YieldsSupermercadoLeaves_ExcludesMarcas()
    {
        var (scraper, handler) = CreateScraper();
        handler.SetupRoute("sitemap_index.xml", LoadFixture("continente-sitemap-index.xml"));
        handler.SetupRoute("sitemap-custom_sitemap_23-category.xml", TrimmedCategorySitemap());
        handler.SetupRoute("start=", "<html><body></body></html>");

        var result = await scraper.ScrapeProductsAsync(null);

        var keys = result.Outcomes.Select(o => o.Key).ToList();
        Assert.NotEmpty(keys);
        Assert.Contains(keys, k => k.StartsWith("supermercado/"));
        Assert.DoesNotContain(keys, k => k.StartsWith("marcas"));
        Assert.All(keys, k => Assert.DoesNotContain(keys, o => o != k && o.StartsWith(k + "/")));
        Assert.DoesNotContain(result.Outcomes, o => o.Kind == OutcomeKind.Discovery);
    }

    [Fact]
    public async Task UncoveredTopLevel_Warns()
    {
        var (scraper, handler) = CreateScraper();
        handler.SetupRoute("sitemap_index.xml",
            "<sitemapindex xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\"><sitemap><loc>https://www.continente.pt/sitemap-categories.xml</loc></sitemap></sitemapindex>");
        handler.SetupRoute("sitemap-categories.xml", Urlset("/supermercado/mercearia/arroz/", "/marcas/x/", "/novo-topo/y/"));
        handler.SetupRoute("start=", "<html><body></body></html>");

        var result = await scraper.ScrapeProductsAsync(null);

        Assert.Contains("Continente: uncovered top-level category 'novo-topo' (1 URLs)", result.Warnings);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("marcas") || w.Contains("supermercado"));
    }

    [Fact]
    public async Task SitemapFailure_DiscoveryFailed()
    {
        var (scraper, handler) = CreateScraper();
        handler.SetupRoute("sitemap_index.xml", "error", HttpStatusCode.InternalServerError);

        var result = await scraper.ScrapeProductsAsync(null);

        var o = Assert.Single(result.Outcomes);
        Assert.Equal(OutcomeKind.Discovery, o.Kind);
        Assert.Equal(OutcomeResult.Failed, o.Result);
        Assert.Equal(ScrapingJobStatus.Failed, ScrapeHealthEvaluator.Evaluate(result).Status);
        Assert.Empty(result.Products);
    }

    [Fact]
    public async Task SupermercadoPage_ParsesTilesAndPaginates()
    {
        var (scraper, handler) = CreateScraper();
        handler.SetupRoute("start=0", LoadFixture("continente-supermercado-page.html"));
        handler.SetupRoute("start=48", "<html><body></body></html>");

        var result = await scraper.ScrapeProductsAsync(LeafPath);

        // The captured page has 40 tiles (< page size 48), so the scraper stops after one request.
        Assert.NotEmpty(result.Products);
        Assert.True(result.Products.Count <= 40);
        Assert.Equal(OutcomeResult.Ok, Assert.Single(result.Outcomes).Result);
        Assert.Equal(1, handler.RequestCount);
    }
}
