using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Savvori.Api.Tests.Infrastructure;
using Savvori.Shared;

namespace Savvori.Api.Tests;

public class ShoppingListsTests : IClassFixture<SavvoriWebApiFactory>
{
    private readonly SavvoriWebApiFactory _factory;
    private readonly Guid _productId;
    private readonly Guid _existingListId;

    public ShoppingListsTests(SavvoriWebApiFactory factory)
    {
        _factory = factory;

        _productId = Guid.NewGuid();
        _existingListId = Guid.NewGuid();

        factory.SeedData(db =>
        {
            var product = TestDataSeeder.CreateTestProduct("Test Product");
            product.Id = _productId;
            db.Products.Add(product);

            var list = TestDataSeeder.CreateTestShoppingList("My List");
            list.Id = _existingListId;
            db.ShoppingLists.Add(list);
        });
    }

    [Fact]
    public async Task GetLists_ReturnsLists()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/shoppinglists", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(body.EnumerateArray().Any());
    }

    [Fact]
    public async Task CreateList_Returns200WithList()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/shoppinglists",
            new { Name = "New Test List" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("New Test List", body.GetProperty("name").GetString());
    }

    [Fact]
    public async Task UpdateList_Returns200WithUpdatedName()
    {
        using var client = _factory.CreateClient();
        var response = await client.PutAsJsonAsync($"/api/shoppinglists/{_existingListId}",
            new { Name = "Updated List Name" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("Updated List Name", body.GetProperty("name").GetString());
    }

    [Fact]
    public async Task UpdateList_NonExistentList_Returns404()
    {
        using var client = _factory.CreateClient();
        var response = await client.PutAsJsonAsync($"/api/shoppinglists/{Guid.NewGuid()}",
            new { Name = "Nope" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteList_Returns204()
    {
        // Create a fresh list to delete
        Guid deleteId = Guid.NewGuid();
        _factory.SeedData(db =>
        {
            var list = TestDataSeeder.CreateTestShoppingList("To Delete");
            list.Id = deleteId;
            db.ShoppingLists.Add(list);
        });

        using var client = _factory.CreateClient();
        var response = await client.DeleteAsync($"/api/shoppinglists/{deleteId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task DeleteList_NonExistentList_Returns404()
    {
        using var client = _factory.CreateClient();
        var response = await client.DeleteAsync($"/api/shoppinglists/{Guid.NewGuid()}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AddItem_Returns200WithItem()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            $"/api/shoppinglists/{_existingListId}/items",
            new { ProductId = _productId, Quantity = 2 }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(_productId.ToString(), body.GetProperty("productId").GetString());
        Assert.Equal(2, body.GetProperty("quantity").GetInt32());
    }

    [Fact]
    public async Task AddItem_NonExistentList_Returns404()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            $"/api/shoppinglists/{Guid.NewGuid()}/items",
            new { ProductId = _productId, Quantity = 1 }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RemoveItem_Returns204()
    {
        // Seed a list with item to remove
        Guid listId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        _factory.SeedData(db =>
        {
            var list = TestDataSeeder.CreateTestShoppingList("List With Item");
            list.Id = listId;
            db.ShoppingLists.Add(list);
            var item = TestDataSeeder.CreateTestShoppingListItem(listId, _productId);
            item.Id = itemId;
            db.ShoppingListItems.Add(item);
        });

        using var client = _factory.CreateClient();
        var response = await client.DeleteAsync($"/api/shoppinglists/{listId}/items/{itemId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task RemoveItem_NonExistentItem_Returns404()
    {
        using var client = _factory.CreateClient();
        var response = await client.DeleteAsync(
            $"/api/shoppinglists/{_existingListId}/items/{Guid.NewGuid()}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AddItem_DuplicateProduct_Returns409()
    {
        _factory.SeedData(db =>
        {
            db.ShoppingListItems.Add(TestDataSeeder.CreateTestShoppingListItem(_existingListId, _productId));
        });

        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            $"/api/shoppinglists/{_existingListId}/items",
            new { ProductId = _productId, Quantity = 1 }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task AddItem_MergedAwayProduct_ResolvesToSurvivorAndSucceeds()
    {
        // The retired id must NOT exist as an active Product row — that's what forces
        // ProductMergeResolver to actually walk the MatchMerge chain instead of short-circuiting.
        var retiredProductId = Guid.NewGuid();
        _factory.SeedData(db =>
        {
            db.MatchMerges.Add(new MatchMerge
            {
                Id = Guid.NewGuid(),
                CandidateId = Guid.NewGuid(),
                SurvivorProductId = _productId,
                RetiredProductId = retiredProductId,
                Method = "test",
                AppliedAt = DateTime.UtcNow
            });
        });

        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            $"/api/shoppinglists/{_existingListId}/items",
            new { ProductId = retiredProductId, Quantity = 3 }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(_productId.ToString(), body.GetProperty("productId").GetString());
    }

    [Fact]
    public async Task UpsertItemQuantity_CreatesWhenMissing_ThenReplacesQuantityIdempotently()
    {
        using var client = _factory.CreateClient();

        var first = await client.PutAsJsonAsync(
            $"/api/shoppinglists/{_existingListId}/items/{_productId}",
            new { Quantity = 2 }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(2, firstBody.GetProperty("quantity").GetInt32());
        var itemId = firstBody.GetProperty("id").GetString();

        var second = await client.PutAsJsonAsync(
            $"/api/shoppinglists/{_existingListId}/items/{_productId}",
            new { Quantity = 5 }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(5, secondBody.GetProperty("quantity").GetInt32());
        Assert.Equal(itemId, secondBody.GetProperty("id").GetString()); // same row, not a second one
    }

    [Fact]
    public async Task UpsertItemQuantity_NonExistentProduct_Returns404()
    {
        using var client = _factory.CreateClient();
        var response = await client.PutAsJsonAsync(
            $"/api/shoppinglists/{_existingListId}/items/{Guid.NewGuid()}",
            new { Quantity = 1 }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Regression (2026-09-23): AddItem/UpsertItemQuantity/SetItemBought all resolve the product
    // through ProductMergeResolver first, which loads Product.ProductCategory into the request's
    // DbContext. Returning the raw tracked ShoppingListItem entity then triggered EF's navigation
    // fixup (item.Product -> ProductCategory -> Products -> back to the same product), and
    // System.Text.Json threw "a possible object cycle was detected" serializing it — but only for
    // a product that actually has a category, which every uncategorized TestDataSeeder product in
    // the tests above doesn't. These three tests use a categorized product specifically to pin
    // against that class of bug recurring.
    [Fact]
    public async Task AddItem_ForACategorizedProduct_DoesNotThrowOnSerialization()
    {
        var catId = Guid.NewGuid();
        var categorizedProductId = Guid.NewGuid();
        _factory.SeedData(db =>
        {
            var category = TestDataSeeder.CreateTestCategory("Snacks", $"snacks-{Guid.NewGuid():N}");
            category.Id = catId;
            db.ProductCategories.Add(category);

            var product = TestDataSeeder.CreateTestProduct("Categorized Product", catId);
            product.Id = categorizedProductId;
            db.Products.Add(product);
        });

        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            $"/api/shoppinglists/{_existingListId}/items",
            new { ProductId = categorizedProductId, Quantity = 1 }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(categorizedProductId.ToString(), body.GetProperty("productId").GetString());
    }

    [Fact]
    public async Task UpsertItemQuantity_ForACategorizedProduct_DoesNotThrowOnSerialization()
    {
        var catId = Guid.NewGuid();
        var categorizedProductId = Guid.NewGuid();
        _factory.SeedData(db =>
        {
            var category = TestDataSeeder.CreateTestCategory("Snacks", $"snacks-{Guid.NewGuid():N}");
            category.Id = catId;
            db.ProductCategories.Add(category);

            var product = TestDataSeeder.CreateTestProduct("Categorized Upsert Product", catId);
            product.Id = categorizedProductId;
            db.Products.Add(product);
        });

        using var client = _factory.CreateClient();
        var response = await client.PutAsJsonAsync(
            $"/api/shoppinglists/{_existingListId}/items/{categorizedProductId}",
            new { Quantity = 3 }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(3, body.GetProperty("quantity").GetInt32());
    }

    [Fact]
    public async Task SetItemBought_ForACategorizedProduct_DoesNotThrowOnSerialization()
    {
        var catId = Guid.NewGuid();
        var categorizedProductId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        _factory.SeedData(db =>
        {
            var category = TestDataSeeder.CreateTestCategory("Snacks", $"snacks-{Guid.NewGuid():N}");
            category.Id = catId;
            db.ProductCategories.Add(category);

            var product = TestDataSeeder.CreateTestProduct("Categorized Bought Product", catId);
            product.Id = categorizedProductId;
            db.Products.Add(product);

            var item = TestDataSeeder.CreateTestShoppingListItem(_existingListId, categorizedProductId);
            item.Id = itemId;
            db.ShoppingListItems.Add(item);
        });

        using var client = _factory.CreateClient();
        var response = await client.PutAsJsonAsync(
            $"/api/shoppinglists/{_existingListId}/items/{itemId}/bought",
            new { Bought = true }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
