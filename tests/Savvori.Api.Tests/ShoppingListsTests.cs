using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Savvori.Api.Tests.Infrastructure;

namespace Savvori.Api.Tests;

public class ShoppingListsTests : IClassFixture<SavvoriWebApiFactory>
{
    private readonly SavvoriWebApiFactory _factory;
    private readonly Guid _userId;
    private readonly Guid _otherUserId;
    private readonly Guid _productId;
    private readonly Guid _existingListId;

    public ShoppingListsTests(SavvoriWebApiFactory factory)
    {
        _factory = factory;

        _userId = Guid.NewGuid();
        _otherUserId = Guid.NewGuid();
        _productId = Guid.NewGuid();
        _existingListId = Guid.NewGuid();

        factory.SeedData(db =>
        {
            var user = TestDataSeeder.CreateTestUser($"user_{_userId}@test.com");
            user.Id = _userId;
            db.Users.Add(user);

            var other = TestDataSeeder.CreateTestUser($"other_{_otherUserId}@test.com");
            other.Id = _otherUserId;
            db.Users.Add(other);

            var product = TestDataSeeder.CreateTestProduct("Test Product");
            product.Id = _productId;
            db.Products.Add(product);

            var list = TestDataSeeder.CreateTestShoppingList(_userId, "My List");
            list.Id = _existingListId;
            db.ShoppingLists.Add(list);

            var otherList = TestDataSeeder.CreateTestShoppingList(_otherUserId, "Other User List");
            db.ShoppingLists.Add(otherList);
        });
    }

    private HttpClient AuthClient(bool asAdmin = false) =>
        _factory.CreateAuthenticatedClient(_userId, $"user_{_userId}@test.com", asAdmin);

    [Fact]
    public async Task GetLists_Authenticated_ReturnsOnlyUserLists()
    {
        using var client = AuthClient();
        var response = await client.GetAsync("/api/shoppinglists");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var lists = body.EnumerateArray().ToList();
        Assert.True(lists.Count >= 1);
        // All lists must belong to the authenticated user
        foreach (var list in lists)
            Assert.Equal(_userId.ToString(), list.GetProperty("userId").GetString());
    }

    [Fact]
    public async Task GetLists_Unauthenticated_Returns401()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/shoppinglists");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateList_Authenticated_Returns200WithList()
    {
        using var client = AuthClient();
        var response = await client.PostAsJsonAsync("/api/shoppinglists",
            new { Name = "New Test List" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("New Test List", body.GetProperty("name").GetString());
        Assert.Equal(_userId.ToString(), body.GetProperty("userId").GetString());
    }

    [Fact]
    public async Task CreateList_Unauthenticated_Returns401()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/shoppinglists",
            new { Name = "Unauthorized List" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UpdateList_OwnList_Returns200WithUpdatedName()
    {
        using var client = AuthClient();
        var response = await client.PutAsJsonAsync($"/api/shoppinglists/{_existingListId}",
            new { Name = "Updated List Name" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Updated List Name", body.GetProperty("name").GetString());
    }

    [Fact]
    public async Task UpdateList_AnotherUsersList_Returns404()
    {
        // Create a list belonging to other user
        Guid otherListId = Guid.NewGuid();
        _factory.SeedData(db =>
        {
            var list = TestDataSeeder.CreateTestShoppingList(_otherUserId, "Other's List");
            list.Id = otherListId;
            db.ShoppingLists.Add(list);
        });

        using var client = AuthClient(); // authenticated as _userId
        var response = await client.PutAsJsonAsync($"/api/shoppinglists/{otherListId}",
            new { Name = "Hijacked Name" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateList_Unauthenticated_Returns401()
    {
        using var client = _factory.CreateClient();
        var response = await client.PutAsJsonAsync($"/api/shoppinglists/{_existingListId}",
            new { Name = "Fail" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeleteList_OwnList_Returns204()
    {
        // Create a fresh list to delete
        Guid deleteId = Guid.NewGuid();
        _factory.SeedData(db =>
        {
            var list = TestDataSeeder.CreateTestShoppingList(_userId, "To Delete");
            list.Id = deleteId;
            db.ShoppingLists.Add(list);
        });

        using var client = AuthClient();
        var response = await client.DeleteAsync($"/api/shoppinglists/{deleteId}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task DeleteList_AnotherUsersList_Returns404()
    {
        Guid otherListId = Guid.NewGuid();
        _factory.SeedData(db =>
        {
            var list = TestDataSeeder.CreateTestShoppingList(_otherUserId, "Not Mine");
            list.Id = otherListId;
            db.ShoppingLists.Add(list);
        });

        using var client = AuthClient();
        var response = await client.DeleteAsync($"/api/shoppinglists/{otherListId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AddItem_OwnList_Returns200WithItem()
    {
        using var client = AuthClient();
        var response = await client.PostAsJsonAsync(
            $"/api/shoppinglists/{_existingListId}/items",
            new { ProductId = _productId, Quantity = 2 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(_productId.ToString(), body.GetProperty("productId").GetString());
        Assert.Equal(2, body.GetProperty("quantity").GetInt32());
    }

    [Fact]
    public async Task AddItem_AnotherUsersList_Returns404()
    {
        Guid otherListId = Guid.NewGuid();
        _factory.SeedData(db =>
        {
            var list = TestDataSeeder.CreateTestShoppingList(_otherUserId, "Other List");
            list.Id = otherListId;
            db.ShoppingLists.Add(list);
        });

        using var client = AuthClient();
        var response = await client.PostAsJsonAsync(
            $"/api/shoppinglists/{otherListId}/items",
            new { ProductId = _productId, Quantity = 1 });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RemoveItem_OwnList_Returns204()
    {
        // Seed a list with item to remove
        Guid listId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        _factory.SeedData(db =>
        {
            var list = TestDataSeeder.CreateTestShoppingList(_userId, "List With Item");
            list.Id = listId;
            db.ShoppingLists.Add(list);
            var item = TestDataSeeder.CreateTestShoppingListItem(listId, _productId);
            item.Id = itemId;
            db.ShoppingListItems.Add(item);
        });

        using var client = AuthClient();
        var response = await client.DeleteAsync($"/api/shoppinglists/{listId}/items/{itemId}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task RemoveItem_NonExistentItem_Returns404()
    {
        using var client = AuthClient();
        var response = await client.DeleteAsync(
            $"/api/shoppinglists/{_existingListId}/items/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
