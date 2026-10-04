using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Savvori.Api.Tests.Infrastructure;
using Savvori.Shared;

namespace Savvori.Api.Tests;

public class ProductsTests : IClassFixture<SavvoriWebApiFactory>
{
    private readonly SavvoriWebApiFactory _factory;
    private readonly HttpClient _client;

    // Shared seed data
    private readonly Guid _categoryId;
    private readonly Guid _productId;
    private readonly string _chainSlug = $"continente-{Guid.NewGuid():N}";

    public ProductsTests(SavvoriWebApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();

        var catId = Guid.NewGuid();
        var prodId = Guid.NewGuid();
        var chainId = Guid.NewGuid();
        _categoryId = catId;
        _productId = prodId;

        factory.SeedData(db =>
        {
            var cat = TestDataSeeder.CreateTestCategory("Dairy", $"dairy-{catId:N}");
            cat.Id = catId;
            db.ProductCategories.Add(cat);

            var chain = TestDataSeeder.CreateTestStoreChain("Continente", _chainSlug);
            chain.Id = chainId;
            db.StoreChains.Add(chain);

            var milk = TestDataSeeder.CreateTestProduct("Milk Full Fat", catId, "Brand A");
            milk.Id = prodId;
            milk.NormalizedName = "milk full fat";
            db.Products.Add(milk);

            // Second product in same category (for alternatives)
            db.Products.Add(TestDataSeeder.CreateTestProduct("Skim Milk", catId, "Brand B"));

            // Third product in different category
            db.Products.Add(TestDataSeeder.CreateTestProduct("Orange Juice"));

            var sp = TestDataSeeder.CreateTestStoreProduct(chainId, prodId);
            db.StoreProducts.Add(sp);
            db.StoreProductPrices.Add(TestDataSeeder.CreateTestStoreProductPrice(sp.Id, 1.29m, isLatest: true));
            // Historical price
            db.StoreProductPrices.Add(TestDataSeeder.CreateTestStoreProductPrice(sp.Id, 1.49m, isLatest: false, scrapedAt: DateTime.UtcNow.AddDays(-5)));
        });
    }

    [Fact]
    public async Task GetProducts_ReturnsPagedResults()
    {
        var response = await _client.GetAsync("/api/products", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(body.GetProperty("total").GetInt32() >= 1);
        Assert.True(body.GetProperty("items").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task GetProducts_WithSearch_ReturnsFilteredResults()
    {
        var response = await _client.GetAsync("/api/products?search=milk", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var items = body.GetProperty("items");
        Assert.True(items.GetArrayLength() >= 1);
        foreach (var item in items.EnumerateArray())
        {
            var name = item.GetProperty("name").GetString() ?? "";
            Assert.Contains("milk", name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task GetProducts_WithCategoryFilter_ReturnsFilteredResults()
    {
        var response = await _client.GetAsync($"/api/products?category={_categoryId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(body.GetProperty("total").GetInt32() >= 1);
    }

    [Fact]
    public async Task GetProducts_Pagination_ReturnsCorrectPageSize()
    {
        var response = await _client.GetAsync("/api/products?page=1&pageSize=1", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(1, body.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, body.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task GetProduct_ValidId_ReturnsProductWithPrices()
    {
        var response = await _client.GetAsync($"/api/products/{_productId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(_productId.ToString(), body.GetProperty("id").GetString());
        Assert.Equal("Milk Full Fat", body.GetProperty("name").GetString());
        var prices = body.GetProperty("prices");
        Assert.True(prices.GetArrayLength() >= 1);
    }

    [Fact]
    public async Task GetProduct_InvalidId_Returns404()
    {
        var response = await _client.GetAsync($"/api/products/{Guid.NewGuid()}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetAlternatives_ValidProduct_ReturnsAlternatives()
    {
        var response = await _client.GetAsync($"/api/products/{_productId}/alternatives", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(body.TryGetProperty("items", out var items2));
        // Should find Skim Milk in same category
        Assert.True(items2.GetArrayLength() >= 1);
    }

    [Fact]
    public async Task GetAlternatives_ProductWithNoCategory_ReturnsEmpty()
    {
        // Orange Juice has no category
        // Find it by name
        var listResponse = await _client.GetAsync("/api/products?search=Orange+Juice", TestContext.Current.CancellationToken);
        var listBody = await listResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var ojId = listBody.GetProperty("items")[0].GetProperty("id").GetString();

        var response = await _client.GetAsync($"/api/products/{ojId}/alternatives", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(0, body.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task GetAlternatives_InvalidProduct_Returns404()
    {
        var response = await _client.GetAsync($"/api/products/{Guid.NewGuid()}/alternatives", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetAlternatives_RanksPricedProductsBeforeUnpriced()
    {
        // Regression test: SQLite sorts NULL first in ascending order, so a plain
        // OrderBy(LowestPrice) would let unpriced alternatives crowd out priced ones.
        var catId = Guid.NewGuid();
        var pricedId = Guid.NewGuid();
        var unpricedId = Guid.NewGuid();
        var anchorId = Guid.NewGuid();
        var chainId = Guid.NewGuid();

        _factory.SeedData(db =>
        {
            var cat = TestDataSeeder.CreateTestCategory("Ranking", $"ranking-{Guid.NewGuid():N}");
            cat.Id = catId;
            db.ProductCategories.Add(cat);

            var chain = TestDataSeeder.CreateTestStoreChain("Ranking Chain", $"ranking-chain-{Guid.NewGuid():N}");
            chain.Id = chainId;
            db.StoreChains.Add(chain);

            var anchor = TestDataSeeder.CreateTestProduct("Anchor", catId);
            anchor.Id = anchorId;
            db.Products.Add(anchor);

            // Registered first, so it would sort before the priced one under a naive ASC ordering.
            var unpriced = TestDataSeeder.CreateTestProduct("Unpriced Alternative", catId);
            unpriced.Id = unpricedId;
            db.Products.Add(unpriced);

            var priced = TestDataSeeder.CreateTestProduct("Priced Alternative", catId);
            priced.Id = pricedId;
            db.Products.Add(priced);
            var sp = TestDataSeeder.CreateTestStoreProduct(chainId, pricedId);
            db.StoreProducts.Add(sp);
            db.StoreProductPrices.Add(TestDataSeeder.CreateTestStoreProductPrice(sp.Id, 3.00m));
        });

        var response = await _client.GetAsync($"/api/products/{anchorId}/alternatives", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var items = body.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal(pricedId.ToString(), items[0].GetProperty("id").GetString());
        Assert.Equal(unpricedId.ToString(), items[1].GetProperty("id").GetString());
    }

    [Fact]
    public async Task GetPriceHistory_ValidProduct_ReturnsHistory()
    {
        var response = await _client.GetAsync($"/api/products/{_productId}/pricehistory", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(_productId.ToString(), body.GetProperty("productId").GetString());
        Assert.True(body.GetProperty("history").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task GetPriceHistory_WithDaysFilter_LimitsResults()
    {
        var response = await _client.GetAsync($"/api/products/{_productId}/pricehistory?days=3", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        // The historical price is 5 days old, so with days=3 only the latest should appear
        Assert.Equal(3, body.GetProperty("days").GetInt32());
        Assert.Equal(1, body.GetProperty("history").GetArrayLength());
    }

    [Fact]
    public async Task GetPriceHistory_WithChainFilter_FiltersResults()
    {
        var response = await _client.GetAsync(
            $"/api/products/{_productId}/pricehistory?chainSlug={_chainSlug}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(body.GetProperty("history").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task GetPriceHistory_InvalidProduct_Returns404()
    {
        var response = await _client.GetAsync($"/api/products/{Guid.NewGuid()}/pricehistory", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
