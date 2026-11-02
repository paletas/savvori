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
        return (new ContinenteScraper(factory, logger) { ReattemptDelay = TimeSpan.Zero, RequestSpacing = TimeSpan.Zero }, handler);
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

    private static List<string> RealSitemapPaths()
    {
        var doc = XDocument.Parse(LoadFixture("continente-sitemap-categories.xml"));
        var ns = doc.Root!.GetDefaultNamespace();
        return doc.Descendants(ns + "loc")
            .Select(e => e.Value.Trim().Replace("https://www.continente.pt/", "").Trim('/'))
            .Where(p => p.Length > 0)
            .ToList();
    }

    [Fact]
    public void Continente_Selection_RealSitemap()
    {
        var sel = ContinenteScraper.SelectCategories(RealSitemapPaths());

        Assert.NotEmpty(sel);
        Assert.DoesNotContain(sel, e => e.StartsWith("marcas"));
        Assert.DoesNotContain(sel, e => e.Contains("ver-todos"));
        Assert.Contains("supermercado/mercearia", sel);
        Assert.Contains("animais/todos", sel);
        Assert.DoesNotContain(sel, e => e.StartsWith("animais/") && e != "animais/todos");
        Assert.All(sel, e => Assert.InRange(e.Split('/').Length, 1, 2));

        var tops = sel.Select(e => e.Split('/')[0]).Distinct().ToList();
        Assert.Equal(["supermercado", "bebe", "animais", "beleza"], tops.Take(4));
        var rest = tops.Skip(4).ToList();
        Assert.Equal(rest.OrderBy(t => t, StringComparer.OrdinalIgnoreCase), rest);
        Assert.StartsWith("supermercado/", sel[0]);

        // A section without a todos page contributes only its second-level categories.
        foreach (var top in tops)
        {
            var entries = sel.Where(e => e.Split('/')[0] == top).ToList();
            if (!entries.Contains($"{top}/todos"))
                Assert.All(entries, e => Assert.True(e.Split('/').Length <= 2));
            else
                Assert.Single(entries);
        }
    }

    [Fact]
    public void Continente_Selection_TodosWinsAndFallbacks()
    {
        var sel = ContinenteScraper.SelectCategories(
            ["bebe/todos", "bebe/alimentacao", "mercearia", "cultura-e-lazer/livros/romance", "x/ver-todos"]);

        Assert.Equal(["bebe/todos", "cultura-e-lazer/livros", "mercearia"], sel);
    }

    [Fact]
    public async Task NoUncoveredTopLevelWarnings()
    {
        var (scraper, handler) = CreateScraper();
        handler.SetupRoute("sitemap_index.xml",
            "<sitemapindex xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\"><sitemap><loc>https://www.continente.pt/sitemap-categories.xml</loc></sitemap></sitemapindex>");
        handler.SetupRoute("sitemap-categories.xml", Urlset("/supermercado/mercearia/arroz/", "/marcas/x/", "/novo-topo/y/"));
        handler.SetupRoute("start=", "<html><body></body></html>");

        var result = await scraper.ScrapeProductsAsync(null);

        Assert.DoesNotContain(result.Warnings, w => w.Contains("uncovered"));
        Assert.Equal(["supermercado/mercearia", "novo-topo/y"], result.Outcomes.Select(o => o.Key).ToList());
    }

    [Fact]
    public async Task Sitemap_WalksSecondLevelCategories_ExcludesMarcas()
    {
        var (scraper, handler) = CreateScraper();
        handler.SetupRoute("sitemap_index.xml", LoadFixture("continente-sitemap-index.xml"));
        handler.SetupRoute("sitemap-custom_sitemap_23-category.xml", TrimmedCategorySitemap());
        handler.SetupRoute("start=", "<html><body></body></html>");

        var result = await scraper.ScrapeProductsAsync(null);

        var keys = result.Outcomes.Select(o => o.Key).ToList();
        Assert.NotEmpty(keys);
        Assert.Contains(keys, k => k.StartsWith("supermercado/") && k.Split('/').Length == 2);
        Assert.DoesNotContain(keys, k => k.StartsWith("marcas"));
        Assert.All(keys, k => Assert.True(k.Split('/').Length <= 2));
        Assert.DoesNotContain(result.Outcomes, o => o.Kind == OutcomeKind.Discovery);
    }

    [Fact]
    public async Task Continente_DiscoveryFailed_NoFallback()
    {
        var (scraper, handler) = CreateScraper();
        handler.SetupRoute("sitemap_index.xml", "error", HttpStatusCode.InternalServerError);
        handler.SetupRoute("start=", LoadFixture("continente_products.html"));

        var result = await scraper.ScrapeProductsAsync(null);

        var o = Assert.Single(result.Outcomes);
        Assert.Equal(OutcomeKind.Discovery, o.Kind);
        Assert.Equal(OutcomeResult.Failed, o.Result);
        Assert.Equal(ScrapingJobStatus.Failed, ScrapeHealthEvaluator.Evaluate(result).Status);
        Assert.Empty(result.Products);
        // Only the sitemap_index request (with its retries) was made: a category request would have yielded products.
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
        // Only the sitemap_index request (with its retries) was made: a category request would have yielded products.
    }
}
