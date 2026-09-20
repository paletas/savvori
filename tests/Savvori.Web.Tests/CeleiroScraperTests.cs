using NSubstitute;
using Savvori.WebApi.Scraping.Scrapers;
using Savvori.Shared;
using Microsoft.Extensions.Logging;

namespace Savvori.Web.Tests;

public class CeleiroScraperTests
{
    private static string LoadFixture(string filename)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", filename);
        return File.ReadAllText(path);
    }

    private static CeleiroScraper CreateScraper()
    {
        var html = LoadFixture("celeiro_products.html");
        var handler = new FakeHttpMessageHandler();
        // Magento repeats the last page for out-of-range page numbers; the scraper must stop on it.
        handler.SetDefaultResponse(html);

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("celeiro").Returns(new HttpClient(handler));

        return new CeleiroScraper(factory, Substitute.For<ILogger<CeleiroScraper>>());
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesValidProductsOnce_AndStopsOnRepeatedPage()
    {
        var products = await CreateScraper().ScrapeProductsAsync("leite", CancellationToken.None);

        // 2 valid products; the SKU-less item is skipped; repeated pages add nothing
        Assert.Equal(2, products.Count);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesFields()
    {
        var products = await CreateScraper().ScrapeProductsAsync("leite", CancellationToken.None);

        var aveia = products.First(p => p.ExternalId == "130950");
        Assert.Equal("Bebida de Aveia Bio", aveia.Name);
        Assert.Equal("Provamel", aveia.Brand);
        Assert.Equal(2.49m, aveia.Price);
        Assert.Equal(2.49m, aveia.UnitPrice);
        Assert.Equal(ProductUnit.L, aveia.Unit);
        Assert.False(aveia.IsPromotion);
        Assert.Equal("https://www.celeiro.pt/130950-bebida-de-aveia-bio-1-ltr-ltr-provamel", aveia.SourceUrl);
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesKgUnitPrice_AndPromotion()
    {
        var products = await CreateScraper().ScrapeProductsAsync("amendoas", CancellationToken.None);

        var amendoas = products.First(p => p.ExternalId == "16813");
        Assert.Equal(3.11m, amendoas.Price);
        Assert.Equal(15.55m, amendoas.UnitPrice);
        Assert.Equal(ProductUnit.Kg, amendoas.Unit);
        Assert.True(amendoas.IsPromotion);
    }

    [Fact]
    public async Task ScrapeStoreLocationsAsync_ReturnsEmptyList()
    {
        Assert.Empty(await CreateScraper().ScrapeStoreLocationsAsync(CancellationToken.None));
    }

    [Fact]
    public void StoreChainSlugIsCeleiro()
    {
        Assert.Equal("celeiro", CreateScraper().StoreChainSlug);
    }
}
