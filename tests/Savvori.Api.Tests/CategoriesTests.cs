using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Savvori.Api.Tests.Infrastructure;

namespace Savvori.Api.Tests;

public class CategoriesTests : IClassFixture<SavvoriWebApiFactory>
{
    private readonly HttpClient _client;
    private readonly Guid _rootCatId;
    private readonly Guid _childCatId;
    private readonly Guid _productInChildId;

    public CategoriesTests(SavvoriWebApiFactory factory)
    {
        _client = factory.CreateClient();

        var rootId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var prodId = Guid.NewGuid();
        _rootCatId = rootId;
        _childCatId = childId;
        _productInChildId = prodId;

        factory.SeedData(db =>
        {
            var root = TestDataSeeder.CreateTestCategory("Food", "food-cat");
            root.Id = rootId;
            db.ProductCategories.Add(root);

            var child = TestDataSeeder.CreateTestCategory("Beverages", "beverages-cat", rootId);
            child.Id = childId;
            db.ProductCategories.Add(child);

            var product = TestDataSeeder.CreateTestProduct("Water 1L", childId);
            product.Id = prodId;
            db.Products.Add(product);
        });
    }

    [Fact]
    public async Task GetCategories_ReturnsHierarchicalTree()
    {
        var response = await _client.GetAsync("/api/categories");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.True(body.GetArrayLength() >= 1);

        // Find our root category
        var food = body.EnumerateArray()
            .FirstOrDefault(c => c.GetProperty("id").GetString() == _rootCatId.ToString());
        Assert.True(food.ValueKind != JsonValueKind.Undefined, "Root category not found");
        Assert.True(food.GetProperty("children").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task GetCategory_ByGuid_Returns200()
    {
        var response = await _client.GetAsync($"/api/categories/{_rootCatId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(_rootCatId.ToString(), body.GetProperty("id").GetString());
        Assert.Equal("Food", body.GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetCategory_BySlug_Returns200()
    {
        var response = await _client.GetAsync("/api/categories/food-cat");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("food-cat", body.GetProperty("slug").GetString());
    }

    [Fact]
    public async Task GetCategory_Unknown_Returns404()
    {
        var response = await _client.GetAsync($"/api/categories/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetCategoryProducts_ReturnsPagedProducts()
    {
        var response = await _client.GetAsync($"/api/categories/{_childCatId}/products");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("total").GetInt32() >= 1);
        Assert.Equal(_childCatId.ToString(), body.GetProperty("categoryId").GetString());
    }

    [Fact]
    public async Task GetCategoryProducts_Recursive_IncludesSubcategoryProducts()
    {
        // Root "food-cat" has no direct products, but child "beverages-cat" does
        var response = await _client.GetAsync($"/api/categories/{_rootCatId}/products?recursive=true");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("total").GetInt32() >= 1);
    }

    [Fact]
    public async Task GetCategoryProducts_NonRecursive_ExcludesSubcategoryProducts()
    {
        // Root "food-cat" has no direct products
        var response = await _client.GetAsync($"/api/categories/{_rootCatId}/products?recursive=false");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task GetCategoryProducts_UnknownCategory_Returns404()
    {
        var response = await _client.GetAsync($"/api/categories/{Guid.NewGuid()}/products");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
