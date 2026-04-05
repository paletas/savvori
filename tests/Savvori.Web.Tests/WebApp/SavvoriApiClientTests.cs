using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Savvori.WebApp.Services;
using Savvori.WebApp.Services.ApiModels;

namespace Savvori.Web.Tests.WebApp;

public class SavvoriApiClientTests
{
    private static SavvoriApiClient CreateClient(FakeHttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://api.test") };
        return new SavvoriApiClient(http, NullLogger<SavvoriApiClient>.Instance);
    }

    private static string ToJson<T>(T obj) =>
        JsonSerializer.Serialize(obj, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    // ===== Auth =====

    [Fact]
    public async Task LoginAsync_WithValidCredentials_ReturnsToken()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetupRoute("/api/auth/login", ToJson(new { token = "jwt-token-123", isAdmin = false }));
        var client = CreateClient(handler);

        var (success, token, isAdmin, error) = await client.LoginAsync("user@test.com", "password123");

        Assert.True(success);
        Assert.Equal("jwt-token-123", token);
        Assert.False(isAdmin);
        Assert.Null(error);
    }

    [Fact]
    public async Task LoginAsync_WithInvalidCredentials_ReturnsError()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetupRoute("/api/auth/login", "", HttpStatusCode.Unauthorized);
        var client = CreateClient(handler);

        var (success, token, isAdmin, error) = await client.LoginAsync("user@test.com", "wrongpassword");

        Assert.False(success);
        Assert.Null(token);
        Assert.False(isAdmin);
        Assert.Equal("Invalid email or password.", error);
    }

    [Fact]
    public async Task RegisterAsync_WithValidData_ReturnsSuccess()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetupRoute("/api/auth/register", "");
        var client = CreateClient(handler);

        var (success, error) = await client.RegisterAsync("newuser@test.com", "password123");

        Assert.True(success);
        Assert.Null(error);
    }

    [Fact]
    public async Task RegisterAsync_WithDuplicateEmail_ReturnsError()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetupRoute("/api/auth/register", "Email already registered", HttpStatusCode.BadRequest);
        var client = CreateClient(handler);

        var (success, error) = await client.RegisterAsync("existing@test.com", "password123");

        Assert.False(success);
        Assert.Equal("Email already registered", error);
    }

    // ===== Products =====

    [Fact]
    public async Task GetProductsAsync_ReturnsPagedResults()
    {
        var products = new ProductsResponse(1, 20, 2, 1, new List<ProductSummaryDto>
        {
            new(Guid.NewGuid(), "Leite UHT", "Mimosa", "Laticínios", null, null, 3, 1.0m, null, 0.89m),
            new(Guid.NewGuid(), "Leite Magro", "Agros", "Laticínios", null, null, 3, 1.0m, null, 0.79m)
        });
        var handler = new FakeHttpMessageHandler();
        handler.SetupRoute("/api/products", ToJson(products));
        var client = CreateClient(handler);

        var result = await client.GetProductsAsync(search: "leite");

        Assert.NotNull(result);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(1, result.Page);
    }

    [Fact]
    public async Task GetProductsAsync_OnError_ReturnsNull()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetupRoute("/api/products", "", HttpStatusCode.InternalServerError);
        var client = CreateClient(handler);

        var result = await client.GetProductsAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task GetProductAsync_WithValidId_ReturnsProduct()
    {
        var id = Guid.NewGuid();
        var product = new ProductDetailDto(
            id, "Leite UHT", "Mimosa", "Laticínios", null,
            "Laticínios", null, 3, 1.0m, null, "leite uht",
            new List<ProductPriceDto>
            {
                new(Guid.NewGuid(), Guid.NewGuid(), "Continente Lisboa", "continente",
                    0.89m, 0.89m, "EUR", false, null, null, DateTime.UtcNow)
            });
        var handler = new FakeHttpMessageHandler();
        handler.SetupRoute($"/api/products/{id}", ToJson(product));
        var client = CreateClient(handler);

        var result = await client.GetProductAsync(id);

        Assert.NotNull(result);
        Assert.Equal("Leite UHT", result.Name);
        Assert.Single(result.Prices);
        Assert.Equal(0.89m, result.Prices[0].Price);
    }

    [Fact]
    public async Task GetProductAsync_OnError_ReturnsNull()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetupRoute("/api/products/", "", HttpStatusCode.NotFound);
        var client = CreateClient(handler);

        var result = await client.GetProductAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAlternativesAsync_ReturnsAlternativesList()
    {
        var productId = Guid.NewGuid();
        var response = new AlternativesResponse(new List<ProductSummaryDto>
        {
            new(Guid.NewGuid(), "Leite Magro", "Agros", null, null, null, 3, 1.0m, null, 0.75m)
        });
        var handler = new FakeHttpMessageHandler();
        handler.SetupRoute($"/api/products/{productId}/alternatives", ToJson(response));
        var client = CreateClient(handler);

        var result = await client.GetAlternativesAsync(productId);

        Assert.Single(result);
        Assert.Equal("Leite Magro", result[0].Name);
    }

    [Fact]
    public async Task GetAlternativesAsync_OnError_ReturnsEmptyList()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetupRoute("/alternatives", "", HttpStatusCode.InternalServerError);
        var client = CreateClient(handler);

        var result = await client.GetAlternativesAsync(Guid.NewGuid());

        Assert.Empty(result);
    }

    // ===== Categories =====

    [Fact]
    public async Task GetCategoriesAsync_ReturnsCategoryTree()
    {
        var categories = new List<CategoryDto>
        {
            new(Guid.NewGuid(), "Laticínios", "laticinios", null, new List<CategoryDto>
            {
                new(Guid.NewGuid(), "Leite", "leite", null, new List<CategoryDto>())
            })
        };
        var handler = new FakeHttpMessageHandler();
        handler.SetupRoute("/api/categories", ToJson(categories));
        var client = CreateClient(handler);

        var result = await client.GetCategoriesAsync();

        Assert.Single(result);
        Assert.Equal("Laticínios", result[0].Name);
        Assert.Single(result[0].Children);
    }

    [Fact]
    public async Task GetCategoriesAsync_OnError_ReturnsEmptyList()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetupRoute("/api/categories", "", HttpStatusCode.InternalServerError);
        var client = CreateClient(handler);

        var result = await client.GetCategoriesAsync();

        Assert.Empty(result);
    }

    // ===== Stores =====

    [Fact]
    public async Task GetNearbyStoresAsync_WithValidPostalCode_ReturnsStores()
    {
        var response = new NearbyStoresResponse(
            "1000-001", 10, 38.716, -9.139, 2,
            new List<NearbyStoreDto>
            {
                new(Guid.NewGuid(), "Continente Colombo", "continente", "Continente",
                    "Av. Lusiada", "1500-392", "Lisboa", 38.73, -9.17, 1.8),
                new(Guid.NewGuid(), "Pingo Doce Benfica", "pingo-doce", "Pingo Doce",
                    "R. Benguela", "1500-084", "Lisboa", 38.72, -9.18, 2.4)
            });
        var handler = new FakeHttpMessageHandler();
        handler.SetupRoute("/api/stores/nearby", ToJson(response));
        var client = CreateClient(handler);

        var result = await client.GetNearbyStoresAsync("1000-001", 10);

        Assert.NotNull(result);
        Assert.Equal(2, result.StoreCount);
        Assert.Equal("1000-001", result.PostalCode);
    }

    [Fact]
    public async Task GetNearbyStoresAsync_OnError_ReturnsNull()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetupRoute("/api/stores/nearby", "", HttpStatusCode.BadRequest);
        var client = CreateClient(handler);

        var result = await client.GetNearbyStoresAsync("invalid");

        Assert.Null(result);
    }

    // ===== Shopping Lists =====

    [Fact]
    public async Task GetShoppingListsAsync_ReturnsUserLists()
    {
        var lists = new List<ShoppingListDto>
        {
            new(Guid.NewGuid(), Guid.NewGuid(), "Weekly shop",
                DateTime.UtcNow, DateTime.UtcNow, new List<ShoppingListItemDto>()),
            new(Guid.NewGuid(), Guid.NewGuid(), "Party supplies",
                DateTime.UtcNow, DateTime.UtcNow, new List<ShoppingListItemDto>())
        };
        var handler = new FakeHttpMessageHandler();
        handler.SetupRoute("/api/shoppinglists", ToJson(lists));
        var client = CreateClient(handler);

        var result = await client.GetShoppingListsAsync();

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetShoppingListsAsync_OnError_ReturnsEmptyList()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetupRoute("/api/shoppinglists", "", HttpStatusCode.InternalServerError);
        var client = CreateClient(handler);

        var result = await client.GetShoppingListsAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task CreateShoppingListAsync_ReturnsCreatedList()
    {
        var created = new ShoppingListDto(
            Guid.NewGuid(), Guid.NewGuid(), "My new list",
            DateTime.UtcNow, DateTime.UtcNow, new List<ShoppingListItemDto>());
        var handler = new FakeHttpMessageHandler();
        handler.SetupRoute("/api/shoppinglists", ToJson(created));
        var client = CreateClient(handler);

        var result = await client.CreateShoppingListAsync("My new list");

        Assert.NotNull(result);
        Assert.Equal("My new list", result.Name);
    }

    [Fact]
    public async Task DeleteShoppingListAsync_ReturnsTrue_OnSuccess()
    {
        var id = Guid.NewGuid();
        var handler = new FakeHttpMessageHandler();
        handler.SetupRoute($"/api/shoppinglists/{id}", "", HttpStatusCode.NoContent);
        var client = CreateClient(handler);

        var result = await client.DeleteShoppingListAsync(id);

        Assert.True(result);
    }

    [Fact]
    public async Task DeleteShoppingListAsync_ReturnsFalse_OnError()
    {
        var id = Guid.NewGuid();
        var handler = new FakeHttpMessageHandler();
        handler.SetupRoute($"/api/shoppinglists/{id}", "", HttpStatusCode.InternalServerError);
        var client = CreateClient(handler);

        var result = await client.DeleteShoppingListAsync(id);

        Assert.False(result);
    }

    [Fact]
    public async Task UpdateShoppingListAsync_ReturnsTrue_OnSuccess()
    {
        var id = Guid.NewGuid();
        var handler = new FakeHttpMessageHandler();
        handler.SetupRoute($"/api/shoppinglists/{id}", "", HttpStatusCode.OK);
        var client = CreateClient(handler);

        var result = await client.UpdateShoppingListAsync(id, "Updated name");

        Assert.True(result);
    }

    // ===== Admin Scraping =====

    [Fact]
    public async Task GetScrapingStatusAsync_ReturnsJobList()
    {
        var jobs = new List<ScrapingStatusDto>
        {
            new("continente", "Continente", true, DateTime.UtcNow.AddHours(6),
                2, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow, 1250, null),
            new("pingodoce", "Pingo Doce", true, DateTime.UtcNow.AddHours(6),
                null, null, null, 0, null)
        };
        var handler = new FakeHttpMessageHandler();
        handler.SetupRoute("/api/admin/scraping/status", ToJson(jobs));
        var client = CreateClient(handler);

        var result = await client.GetScrapingStatusAsync();

        Assert.Equal(2, result.Count);
        Assert.Equal("continente", result[0].ChainSlug);
        Assert.Equal(2, result[0].LastStatus);  // Completed
        Assert.Null(result[1].LastStatus);       // Never run
    }

    [Fact]
    public async Task GetScrapingStatusAsync_OnError_ReturnsEmptyList()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetupRoute("/api/admin/scraping/status", "", HttpStatusCode.InternalServerError);
        var client = CreateClient(handler);

        var result = await client.GetScrapingStatusAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task TriggerScrapeAsync_ReturnsSuccess_OnAccepted()
    {
        var response = new TriggerResponse("Scraping job triggered for 'continente'", "continente");
        var handler = new FakeHttpMessageHandler();
        handler.SetupRoute("/api/admin/scraping/trigger/continente", ToJson(response), HttpStatusCode.Accepted);
        var client = CreateClient(handler);

        var (success, message) = await client.TriggerScrapeAsync("continente");

        Assert.True(success);
        Assert.Contains("continente", message);
    }

    [Fact]
    public async Task TriggerScrapeAsync_ReturnsFailure_OnBadRequest()
    {
        var handler = new FakeHttpMessageHandler();
        handler.SetupRoute("/api/admin/scraping/trigger/unknown", "No scheduled job found", HttpStatusCode.BadRequest);
        var client = CreateClient(handler);

        var (success, message) = await client.TriggerScrapeAsync("unknown");

        Assert.False(success);
        Assert.NotNull(message);
    }
}
