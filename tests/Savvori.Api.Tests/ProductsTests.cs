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
    private readonly string _chainSlug = "continente";

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
            var cat = TestDataSeeder.CreateTestCategory("Dairy", "dairy");
            cat.Id = catId;
            db.ProductCategories.Add(cat);

            var chain = TestDataSeeder.CreateTestStoreChain("Continente", "continente");
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
        var response = await _client.GetAsync("/api/products");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("total").GetInt32() >= 1);
        Assert.True(body.GetProperty("items").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task GetProducts_WithSearch_ReturnsFilteredResults()
    {
        var response = await _client.GetAsync("/api/products?search=milk");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
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
        var response = await _client.GetAsync($"/api/products?category={_categoryId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("total").GetInt32() >= 1);
    }

    [Fact]
    public async Task GetProducts_Pagination_ReturnsCorrectPageSize()
    {
        var response = await _client.GetAsync("/api/products?page=1&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, body.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task GetProduct_ValidId_ReturnsProductWithPrices()
    {
        var response = await _client.GetAsync($"/api/products/{_productId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(_productId.ToString(), body.GetProperty("id").GetString());
        Assert.Equal("Milk Full Fat", body.GetProperty("name").GetString());
        var prices = body.GetProperty("prices");
        Assert.True(prices.GetArrayLength() >= 1);
    }

    [Fact]
    public async Task GetProduct_InvalidId_Returns404()
    {
        var response = await _client.GetAsync($"/api/products/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetAlternatives_ValidProduct_ReturnsAlternatives()
    {
        var response = await _client.GetAsync($"/api/products/{_productId}/alternatives");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("items", out var items2));
        // Should find Skim Milk in same category
        Assert.True(items2.GetArrayLength() >= 1);
    }

    [Fact]
    public async Task GetAlternatives_ProductWithNoCategory_ReturnsEmpty()
    {
        // Orange Juice has no category
        // Find it by name
        var listResponse = await _client.GetAsync("/api/products?search=Orange+Juice");
        var listBody = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        var ojId = listBody.GetProperty("items")[0].GetProperty("id").GetString();

        var response = await _client.GetAsync($"/api/products/{ojId}/alternatives");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task GetAlternatives_InvalidProduct_Returns404()
    {
        var response = await _client.GetAsync($"/api/products/{Guid.NewGuid()}/alternatives");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetPriceHistory_ValidProduct_ReturnsHistory()
    {
        var response = await _client.GetAsync($"/api/products/{_productId}/pricehistory");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(_productId.ToString(), body.GetProperty("productId").GetString());
        Assert.True(body.GetProperty("history").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task GetPriceHistory_WithDaysFilter_LimitsResults()
    {
        var response = await _client.GetAsync($"/api/products/{_productId}/pricehistory?days=3");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        // The historical price is 5 days old, so with days=3 only the latest should appear
        Assert.Equal(3, body.GetProperty("days").GetInt32());
        Assert.Equal(1, body.GetProperty("history").GetArrayLength());
    }

    [Fact]
    public async Task GetPriceHistory_WithChainFilter_FiltersResults()
    {
        var response = await _client.GetAsync(
            $"/api/products/{_productId}/pricehistory?chainSlug={_chainSlug}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("history").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task GetPriceHistory_InvalidProduct_Returns404()
    {
        var response = await _client.GetAsync($"/api/products/{Guid.NewGuid()}/pricehistory");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
