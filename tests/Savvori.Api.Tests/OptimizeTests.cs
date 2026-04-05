using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Savvori.Api.Tests.Infrastructure;

namespace Savvori.Api.Tests;

public class OptimizeTests : IClassFixture<SavvoriWebApiFactory>
{
    private readonly SavvoriWebApiFactory _factory;
    private readonly Guid _userId;
    private readonly Guid _otherUserId;
    private readonly Guid _listId;
    private readonly Guid _emptyListId;

    public OptimizeTests(SavvoriWebApiFactory factory)
    {
        _factory = factory;
        _userId = Guid.NewGuid();
        _otherUserId = Guid.NewGuid();
        _listId = Guid.NewGuid();
        _emptyListId = Guid.NewGuid();

        var chainId1 = Guid.NewGuid();
        var chainId2 = Guid.NewGuid();
        var storeId1 = Guid.NewGuid();
        var storeId2 = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var catId = Guid.NewGuid();

        factory.SeedData(db =>
        {
            var user = TestDataSeeder.CreateTestUser($"opt_user_{_userId}@test.com");
            user.Id = _userId;
            db.Users.Add(user);

            var other = TestDataSeeder.CreateTestUser($"opt_other_{_otherUserId}@test.com");
            other.Id = _otherUserId;
            db.Users.Add(other);

            var cat = TestDataSeeder.CreateTestCategory("Groceries", $"groceries-opt-{Guid.NewGuid():N}");
            cat.Id = catId;
            db.ProductCategories.Add(cat);

            var chain1 = TestDataSeeder.CreateTestStoreChain("Store A", $"store-a-{Guid.NewGuid():N}");
            chain1.Id = chainId1;
            db.StoreChains.Add(chain1);

            var chain2 = TestDataSeeder.CreateTestStoreChain("Store B", $"store-b-{Guid.NewGuid():N}");
            chain2.Id = chainId2;
            db.StoreChains.Add(chain2);

            var store1 = TestDataSeeder.CreateTestStore(chainId1, "Store A Lisboa", 38.716, -9.139);
            store1.Id = storeId1;
            db.Stores.Add(store1);

            var store2 = TestDataSeeder.CreateTestStore(chainId2, "Store B Lisboa", 38.720, -9.145);
            store2.Id = storeId2;
            db.Stores.Add(store2);

            var product = TestDataSeeder.CreateTestProduct("Bread", catId);
            product.Id = productId;
            db.Products.Add(product);

            // Product available at both chains with different prices
            var sp1 = TestDataSeeder.CreateTestStoreProduct(chainId1, productId);
            var sp2 = TestDataSeeder.CreateTestStoreProduct(chainId2, productId);
            db.StoreProducts.AddRange(sp1, sp2);
            db.StoreProductPrices.Add(TestDataSeeder.CreateTestStoreProductPrice(sp1.Id, 1.50m));
            db.StoreProductPrices.Add(TestDataSeeder.CreateTestStoreProductPrice(sp2.Id, 1.20m));

            // Shopping list with 1 item
            var list = TestDataSeeder.CreateTestShoppingList(_userId, "Optimize Test List");
            list.Id = _listId;
            db.ShoppingLists.Add(list);
            db.ShoppingListItems.Add(TestDataSeeder.CreateTestShoppingListItem(_listId, productId));

            // Empty shopping list
            var emptyList = TestDataSeeder.CreateTestShoppingList(_userId, "Empty List");
            emptyList.Id = _emptyListId;
            db.ShoppingLists.Add(emptyList);
        });
    }

    private HttpClient AuthClient() =>
        _factory.CreateAuthenticatedClient(_userId, $"opt_user_{_userId}@test.com");

    [Fact]
    public async Task Optimize_CheapestTotal_Returns200WithItems()
    {
        using var client = AuthClient();
        var response = await client.GetAsync($"/api/shoppinglists/{_listId}/optimize?mode=cheapest-total");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("items").GetArrayLength() >= 1);
        Assert.Equal("cheapest-total", body.GetProperty("optimizationMode").GetString());
    }

    [Fact]
    public async Task Optimize_CheapestStore_Returns200WithItems()
    {
        using var client = AuthClient();
        var response = await client.GetAsync($"/api/shoppinglists/{_listId}/optimize?mode=cheapest-store");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("items").GetArrayLength() >= 1);
        Assert.Equal("cheapest-store", body.GetProperty("optimizationMode").GetString());
    }

    [Fact]
    public async Task Optimize_Balanced_Returns200()
    {
        using var client = AuthClient();
        var response = await client.GetAsync(
            $"/api/shoppinglists/{_listId}/optimize?mode=balanced&threshold=0.50");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("items").GetArrayLength() >= 0);
    }

    [Fact]
    public async Task Optimize_Compare_Returns200WithMatrix()
    {
        using var client = AuthClient();
        var response = await client.GetAsync($"/api/shoppinglists/{_listId}/optimize?mode=compare");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("stores", out var stores));
        Assert.True(body.TryGetProperty("rows", out var rows));
        Assert.True(stores.GetArrayLength() >= 1);
        Assert.True(rows.GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Optimize_DefaultMode_UsesCheapestTotal()
    {
        using var client = AuthClient();
        // No mode parameter — defaults to cheapest-total
        var response = await client.GetAsync($"/api/shoppinglists/{_listId}/optimize");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("cheapest-total", body.GetProperty("optimizationMode").GetString());
    }

    [Fact]
    public async Task Optimize_UnknownMode_Returns400()
    {
        using var client = AuthClient();
        var response = await client.GetAsync($"/api/shoppinglists/{_listId}/optimize?mode=invalid-mode");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Optimize_AnotherUsersList_Returns404()
    {
        Guid otherListId = Guid.NewGuid();
        _factory.SeedData(db =>
        {
            var list = TestDataSeeder.CreateTestShoppingList(_otherUserId, "Other Opt List");
            list.Id = otherListId;
            db.ShoppingLists.Add(list);
        });

        using var client = AuthClient(); // authenticated as _userId
        var response = await client.GetAsync($"/api/shoppinglists/{otherListId}/optimize");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Optimize_Unauthenticated_Returns401()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/shoppinglists/{_listId}/optimize");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Optimize_EmptyList_ReturnsEmptyItems()
    {
        using var client = AuthClient();
        var response = await client.GetAsync($"/api/shoppinglists/{_emptyListId}/optimize?mode=cheapest-total");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("items").GetArrayLength());
    }
}
