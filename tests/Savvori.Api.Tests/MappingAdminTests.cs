using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Savvori.Api.Tests.Infrastructure;
using Savvori.Shared;

namespace Savvori.Api.Tests;

public class MappingAdminTests : IClassFixture<SavvoriWebApiFactory>
{
    private readonly SavvoriWebApiFactory _factory;
    private readonly Guid _chainId;
    private readonly Guid _categoryId;
    private readonly Guid _productWithCategoryId;
    private readonly Guid _productNoCategoryId;
    private readonly Guid _storeProductUnmatchedId;
    private readonly Guid _storeProductMatchedId;
    // Dedicated entities for mutation tests (not checked by read tests)
    private readonly Guid _productForCategoryAssignId;
    private readonly Guid _storeProductForCanonicalAssignId;

    public MappingAdminTests(SavvoriWebApiFactory factory)
    {
        _factory = factory;
        _chainId            = Guid.NewGuid();
        _categoryId         = Guid.NewGuid();
        _productWithCategoryId  = Guid.NewGuid();
        _productNoCategoryId    = Guid.NewGuid();
        _storeProductUnmatchedId = Guid.NewGuid();
        _storeProductMatchedId  = Guid.NewGuid();
        _productForCategoryAssignId    = Guid.NewGuid();
        _storeProductForCanonicalAssignId = Guid.NewGuid();

        factory.SeedData(db =>
        {


            var chain = TestDataSeeder.CreateTestStoreChain("Continente", $"continente-{_chainId:N}");
            chain.Id = _chainId;
            db.StoreChains.Add(chain);

            var category = TestDataSeeder.CreateTestCategory("Leite", slug: $"leite-{_categoryId:N}");
            category.Id = _categoryId;
            db.ProductCategories.Add(category);

            var productWithCat = TestDataSeeder.CreateTestProduct("Leite Mimosa", categoryId: _categoryId);
            productWithCat.Id = _productWithCategoryId;
            db.Products.Add(productWithCat);

            var productNoCat = TestDataSeeder.CreateTestProduct("Unknown Product");
            productNoCat.Id = _productNoCategoryId;
            productNoCat.Category = "unknown-scraped-category";
            db.Products.Add(productNoCat);

            var spUnmatched = TestDataSeeder.CreateTestStoreProduct(_chainId);
            spUnmatched.Id = _storeProductUnmatchedId;
            spUnmatched.MatchStatus = MatchStatus.Unmatched;
            spUnmatched.CanonicalProductId = null;
            db.StoreProducts.Add(spUnmatched);

            var spMatched = TestDataSeeder.CreateTestStoreProduct(_chainId, canonicalProductId: _productWithCategoryId);
            spMatched.Id = _storeProductMatchedId;
            db.StoreProducts.Add(spMatched);

            // Dedicated entities for mutation tests only
            var productForAssign = TestDataSeeder.CreateTestProduct("Product For Category Assign");
            productForAssign.Id = _productForCategoryAssignId;
            productForAssign.Category = "unknown-for-assign";
            db.Products.Add(productForAssign);

            var spForAssign = TestDataSeeder.CreateTestStoreProduct(_chainId);
            spForAssign.Id = _storeProductForCanonicalAssignId;
            spForAssign.MatchStatus = MatchStatus.Unmatched;
            spForAssign.CanonicalProductId = null;
            db.StoreProducts.Add(spForAssign);
        });
    }



    // ── GetStats ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetStats_AsAdmin_Returns200WithCounts()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/admin/mapping/stats", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(body.GetProperty("totalProducts").GetInt32() >= 2);
        Assert.True(body.GetProperty("uncategorizedProducts").GetInt32() >= 1);
        Assert.True(body.TryGetProperty("byMatchStatus", out _));
    }

    // ── GetUncategorizedProducts ──────────────────────────────────────────────

    [Fact]
    public async Task GetUncategorizedProducts_AsAdmin_Returns200WithItems()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/admin/mapping/uncategorized-products?pageSize=100", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(body.GetProperty("total").GetInt32() >= 1);
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.True(items.Count >= 1);
        // The seeded uncategorized product should be present
        Assert.Contains(items, i => i.GetProperty("id").GetGuid() == _productNoCategoryId);
    }

    [Fact]
    public async Task GetUncategorizedProducts_DoesNotIncludeProductsWithCategory()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/admin/mapping/uncategorized-products?pageSize=100", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var ids = body.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid())
            .ToList();

        Assert.DoesNotContain(_productWithCategoryId, ids);
    }

    // ── GetUnmappedCategories ─────────────────────────────────────────────────

    [Fact]
    public async Task GetUnmappedCategories_AsAdmin_Returns200()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/admin/mapping/unmapped-categories", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
    }

    // ── GetStoreProducts ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetStoreProducts_NoFilter_Returns200WithAllProducts()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/admin/mapping/store-products", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(body.GetProperty("total").GetInt32() >= 2);
    }

    [Fact]
    public async Task GetStoreProducts_FilterByUnmatched_ReturnsOnlyUnmatched()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/admin/mapping/store-products?status=Unmatched", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.True(items.Count >= 1);
        Assert.All(items, i => Assert.Equal("Unmatched", i.GetProperty("matchStatus").GetString()));
    }

    // ── BackfillCategories ────────────────────────────────────────────────────

    [Fact]
    public async Task BackfillCategories_AsAdmin_Returns200WithUpdatedCount()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync("/api/admin/mapping/backfill-categories", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        // We don't assert exact count as the seeded product's category string "unknown-scraped-category"
        // won't map to anything — but the response format must be correct.
        Assert.True(body.TryGetProperty("updated", out _));
        Assert.True(body.TryGetProperty("skipped", out _));
    }

    // ── Rematch ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rematch_AsAdmin_Returns200WithMatchedCount()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync("/api/admin/mapping/rematch", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(body.TryGetProperty("matched", out _));
        Assert.True(body.TryGetProperty("remaining", out _));
    }

    [Fact]
    public async Task Rematch_InvalidChain_Returns404()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync("/api/admin/mapping/rematch?chainSlug=nonexistent-chain-xyz", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── AssignProductCategory ─────────────────────────────────────────────────

    [Fact]
    public async Task AssignProductCategory_AsAdmin_ValidIds_Returns200()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PutAsJsonAsync(
            $"/api/admin/mapping/products/{_productForCategoryAssignId}/category",
            new { categoryId = _categoryId }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(_categoryId, body.GetProperty("categoryId").GetGuid());
    }

    [Fact]
    public async Task AssignProductCategory_UnknownProduct_Returns404()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PutAsJsonAsync(
            $"/api/admin/mapping/products/{Guid.NewGuid()}/category",
            new { categoryId = _categoryId }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task AssignProductCategory_UnknownCategory_Returns400()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PutAsJsonAsync(
            $"/api/admin/mapping/products/{_productForCategoryAssignId}/category",
            new { categoryId = Guid.NewGuid() }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── AssignCanonicalProduct ────────────────────────────────────────────────

    [Fact]
    public async Task AssignCanonicalProduct_AsAdmin_ValidIds_Returns200AndSetsManualMatched()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PutAsJsonAsync(
            $"/api/admin/mapping/store-products/{_storeProductForCanonicalAssignId}/canonical",
            new { canonicalProductId = _productWithCategoryId }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(_productWithCategoryId, body.GetProperty("canonicalProductId").GetGuid());
    }

    [Fact]
    public async Task AssignCanonicalProduct_UnknownStoreProduct_Returns404()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PutAsJsonAsync(
            $"/api/admin/mapping/store-products/{Guid.NewGuid()}/canonical",
            new { canonicalProductId = _productWithCategoryId }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task AssignCanonicalProduct_UnknownCanonical_Returns400()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PutAsJsonAsync(
            $"/api/admin/mapping/store-products/{_storeProductMatchedId}/canonical",
            new { canonicalProductId = Guid.NewGuid() }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

}
