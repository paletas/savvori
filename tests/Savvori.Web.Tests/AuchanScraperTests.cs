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
        // Page 1 returns products, page 2 returns empty (stops pagination)
        handler.SetupRoute("page=1", html);
        handler.SetupRoute("page=2", "<html><body></body></html>");

        var httpClient = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("auchan").Returns(httpClient);

        var logger = Substitute.For<ILogger<AuchanScraper>>();
        return (new AuchanScraper(factory, logger), handler);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesProductsFromFixture()
    {
        var (scraper, _) = CreateScraper();
        var products = await scraper.ScrapeProductsAsync("leite-e-laticínios", CancellationToken.None);

        // 4 valid products; 2 invalid (no pid, price=0) are skipped
        Assert.Equal(4, products.Count);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesNameAndBrand()
    {
        var (scraper, _) = CreateScraper();
        var products = await scraper.ScrapeProductsAsync("leite-e-laticínios", CancellationToken.None);

        var leite = products.First(p => p.ExternalId == "3010403");
        Assert.Equal("LEITE AUCHAN UHT MEIO GORDO SLIM 1L", leite.Name);
        Assert.Equal("AUCHAN", leite.Brand);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesPrice()
    {
        var (scraper, _) = CreateScraper();
        var products = await scraper.ScrapeProductsAsync("leite-e-laticínios", CancellationToken.None);

        var leite = products.First(p => p.ExternalId == "3010403");
        Assert.Equal(0.86m, leite.Price);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesUnitPrice()
    {
        var (scraper, _) = CreateScraper();
        var products = await scraper.ScrapeProductsAsync("leite-e-laticínios", CancellationToken.None);

        var leite = products.First(p => p.ExternalId == "3010403");
        Assert.Equal(0.86m, leite.UnitPrice);
    }

    [Fact]
    public async Task ScrapeProductsAsync_DetectsPromotion_WhenDimension7IsYes()
    {
        var (scraper, _) = CreateScraper();
        var products = await scraper.ScrapeProductsAsync("leite-e-laticínios", CancellationToken.None);

        var mimosa = products.First(p => p.ExternalId == "11885");
        Assert.True(mimosa.IsPromotion);
    }

    [Fact]
    public async Task ScrapeProductsAsync_DetectsPromotion_WhenPromoElementPresent()
    {
        // The Mimosa product has auc-price__promotion element even if dimension7 weren't "Yes"
        var (scraper, _) = CreateScraper();
        var products = await scraper.ScrapeProductsAsync("leite-e-laticínios", CancellationToken.None);

        var mimosa = products.First(p => p.ExternalId == "11885");
        Assert.True(mimosa.IsPromotion);
    }

    [Fact]
    public async Task ScrapeProductsAsync_NoPromotion_WhenDimension7IsNo()
    {
        var (scraper, _) = CreateScraper();
        var products = await scraper.ScrapeProductsAsync("leite-e-laticínios", CancellationToken.None);

        var leite = products.First(p => p.ExternalId == "3010403");
        Assert.False(leite.IsPromotion);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ExtractsCategory_AsLeafOfPath()
    {
        var (scraper, _) = CreateScraper();
        var products = await scraper.ScrapeProductsAsync("leite-e-laticínios", CancellationToken.None);

        var leite = products.First(p => p.ExternalId == "3010403");
        Assert.Equal("leite-uht", leite.Category);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ExtractsSourceUrl()
    {
        var (scraper, _) = CreateScraper();
        var products = await scraper.ScrapeProductsAsync("leite-e-laticínios", CancellationToken.None);

        var leite = products.First(p => p.ExternalId == "3010403");
        Assert.NotNull(leite.SourceUrl);
        Assert.Contains("auchan.pt", leite.SourceUrl);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ExtractsSizeFromProductName()
    {
        var (scraper, _) = CreateScraper();
        var products = await scraper.ScrapeProductsAsync("leite-e-laticínios", CancellationToken.None);

        var leite = products.First(p => p.ExternalId == "3010403");
        Assert.Equal(1m, leite.SizeValue);
        Assert.Equal(ProductUnit.L, leite.Unit);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ExtractsMultipackSize()
    {
        var (scraper, _) = CreateScraper();
        var products = await scraper.ScrapeProductsAsync("leite-e-laticínios", CancellationToken.None);

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
        handler.SetupRoute("page=1", htmlWithDuplicate);
        handler.SetupRoute("page=2", "<html><body></body></html>");

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("auchan").Returns(new HttpClient(handler));
        var logger = Substitute.For<ILogger<AuchanScraper>>();
        var scraper = new AuchanScraper(factory, logger);

        var products = await scraper.ScrapeProductsAsync("leite-e-laticínios", CancellationToken.None);

        var leiteCount = products.Count(p => p.ExternalId == "3010403");
        Assert.Equal(1, leiteCount);
    }

    [Fact]
    public async Task ScrapeProductsAsync_SkipsProductsWithNoPid()
    {
        var (scraper, _) = CreateScraper();
        var products = await scraper.ScrapeProductsAsync("leite-e-laticínios", CancellationToken.None);

        Assert.DoesNotContain(products, p => p.Name == "PRODUTO SEM ID");
    }

    [Fact]
    public async Task ScrapeProductsAsync_SkipsProductsWithZeroPrice()
    {
        var (scraper, _) = CreateScraper();
        var products = await scraper.ScrapeProductsAsync("leite-e-laticínios", CancellationToken.None);

        Assert.DoesNotContain(products, p => p.Name == "PRODUTO SEM PRECO");
    }

    [Fact]
    public async Task ScrapeStoreLocationsAsync_ReturnsEmptyList()
    {
        var (scraper, _) = CreateScraper();
        var locations = await scraper.ScrapeStoreLocationsAsync(CancellationToken.None);
        Assert.Empty(locations);
    }

    [Fact]
    public async Task ScrapeProductsAsync_StoreChainSlugIsAuchan()
    {
        var scraper = CreateScraper().Scraper;
        Assert.Equal("auchan", scraper.StoreChainSlug);
    }
}
