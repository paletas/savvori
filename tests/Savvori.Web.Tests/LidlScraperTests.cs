using NSubstitute;
using Savvori.WebApi.Scraping.Scrapers;
using Savvori.Shared;
using Microsoft.Extensions.Logging;

namespace Savvori.Web.Tests;

public class LidlScraperTests
{
    private static LidlScraper CreateScraper()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lidl_search.json"));
        var handler = new FakeHttpMessageHandler();
        handler.SetDefaultResponse(json);

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("lidl").Returns(new HttpClient(handler));

        return new LidlScraper(factory, Substitute.For<ILogger<LidlScraper>>());
    }

    [Fact]
    public async Task ScrapeProductsAsync_KeepsOnlyPricedFoodItems()
    {
        var products = await CreateScraper().ScrapeProductsAsync(null, CancellationToken.None);

        // Non-food and price-less items are skipped
        Assert.Equal(2, products.Count);
        Assert.DoesNotContain(products, p => p.ExternalId == "20000001");
        Assert.DoesNotContain(products, p => p.ExternalId == "11500002");
    }

    [Fact]
    public async Task ScrapeProductsAsync_ParsesPromotedProduct()
    {
        var products = await CreateScraper().ScrapeProductsAsync(null, CancellationToken.None);

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
        var products = await CreateScraper().ScrapeProductsAsync(null, CancellationToken.None);

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
