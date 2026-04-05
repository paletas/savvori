using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Savvori.WebApp.Services.ApiModels;

namespace Savvori.WebApp.Services;

public class SavvoriApiClient(HttpClient http, ILogger<SavvoriApiClient> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // ===== Auth =====

    public async Task<(bool Success, string? Token, bool IsAdmin, string? Error)> LoginAsync(
        string email, string password, CancellationToken ct = default)
    {
        try
        {
            var resp = await http.PostAsJsonAsync("/api/auth/login", new { email, password }, ct);
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                return (false, null, false, "Invalid email or password.");
            resp.EnsureSuccessStatusCode();
            var result = await resp.Content.ReadFromJsonAsync<LoginResponse>(JsonOptions, ct);
            return (true, result?.Token, result?.IsAdmin ?? false, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Login failed");
            return (false, null, false, "An error occurred during login.");
        }
    }

    public async Task<(bool Success, string? Error)> RegisterAsync(
        string email, string password, CancellationToken ct = default)
    {
        try
        {
            var resp = await http.PostAsJsonAsync("/api/auth/register", new { email, password }, ct);
            if (resp.StatusCode == HttpStatusCode.BadRequest)
            {
                var error = await resp.Content.ReadAsStringAsync(ct);
                return (false, error);
            }
            resp.EnsureSuccessStatusCode();
            return (true, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Register failed");
            return (false, "An error occurred during registration.");
        }
    }

    // ===== Categories =====

    public async Task<List<CategoryDto>> GetCategoriesAsync(CancellationToken ct = default)
    {
        try
        {
            return await http.GetFromJsonAsync<List<CategoryDto>>("/api/categories", JsonOptions, ct) ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get categories");
            return [];
        }
    }

    public async Task<CategoryDto?> GetCategoryAsync(string idOrSlug, CancellationToken ct = default)
    {
        try
        {
            return await http.GetFromJsonAsync<CategoryDto>($"/api/categories/{Uri.EscapeDataString(idOrSlug)}", JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get category {IdOrSlug}", idOrSlug);
            return null;
        }
    }

    public async Task<CategoryProductsResponse?> GetCategoryProductsAsync(
        string idOrSlug, int page = 1, int pageSize = 20, bool recursive = false, CancellationToken ct = default)
    {
        try
        {
            var url = $"/api/categories/{Uri.EscapeDataString(idOrSlug)}/products?page={page}&pageSize={pageSize}&recursive={recursive}";
            return await http.GetFromJsonAsync<CategoryProductsResponse>(url, JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get category products for {IdOrSlug}", idOrSlug);
            return null;
        }
    }

    // ===== Products =====

    public async Task<ProductsResponse?> GetProductsAsync(
        string? search = null, Guid? category = null, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        try
        {
            var qs = new List<string> { $"page={page}", $"pageSize={pageSize}" };
            if (!string.IsNullOrWhiteSpace(search)) qs.Add($"search={Uri.EscapeDataString(search)}");
            if (category.HasValue) qs.Add($"category={category.Value}");
            return await http.GetFromJsonAsync<ProductsResponse>($"/api/products?{string.Join('&', qs)}", JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get products");
            return null;
        }
    }

    public async Task<ProductDetailDto?> GetProductAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            return await http.GetFromJsonAsync<ProductDetailDto>($"/api/products/{id}", JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get product {Id}", id);
            return null;
        }
    }

    public async Task<List<ProductSummaryDto>> GetAlternativesAsync(Guid productId, CancellationToken ct = default)
    {
        try
        {
            var resp = await http.GetFromJsonAsync<AlternativesResponse>($"/api/products/{productId}/alternatives", JsonOptions, ct);
            return resp?.Items ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get alternatives for {ProductId}", productId);
            return [];
        }
    }

    public async Task<PriceHistoryResponse?> GetPriceHistoryAsync(
        Guid productId, Guid? storeId = null, int days = 30, CancellationToken ct = default)
    {
        try
        {
            var url = $"/api/products/{productId}/pricehistory?days={days}";
            if (storeId.HasValue) url += $"&storeId={storeId.Value}";
            return await http.GetFromJsonAsync<PriceHistoryResponse>(url, JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get price history for {ProductId}", productId);
            return null;
        }
    }

    // ===== Stores =====

    public async Task<List<StoreChainDto>> GetStoresAsync(string? chain = null, CancellationToken ct = default)
    {
        try
        {
            var url = chain != null ? $"/api/stores?chain={Uri.EscapeDataString(chain)}" : "/api/stores";
            return await http.GetFromJsonAsync<List<StoreChainDto>>(url, JsonOptions, ct) ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get stores");
            return [];
        }
    }

    public async Task<NearbyStoresResponse?> GetNearbyStoresAsync(
        string postalCode, double radiusKm = 10, CancellationToken ct = default)
    {
        try
        {
            return await http.GetFromJsonAsync<NearbyStoresResponse>(
                $"/api/stores/nearby?postalCode={Uri.EscapeDataString(postalCode)}&radiusKm={radiusKm}", JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get nearby stores");
            return null;
        }
    }

    public async Task<StoreLocationsResponse?> GetStoreLocationsAsync(string chainSlug, CancellationToken ct = default)
    {
        try
        {
            return await http.GetFromJsonAsync<StoreLocationsResponse>(
                $"/api/stores/{Uri.EscapeDataString(chainSlug)}/locations", JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get store locations for {ChainSlug}", chainSlug);
            return null;
        }
    }

    // ===== Shopping Lists =====

    public async Task<List<ShoppingListDto>> GetShoppingListsAsync(CancellationToken ct = default)
    {
        try
        {
            return await http.GetFromJsonAsync<List<ShoppingListDto>>("/api/shoppinglists", JsonOptions, ct) ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get shopping lists");
            return [];
        }
    }

    public async Task<ShoppingListDto?> CreateShoppingListAsync(string name, CancellationToken ct = default)
    {
        try
        {
            var resp = await http.PostAsJsonAsync("/api/shoppinglists", new { name }, ct);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<ShoppingListDto>(JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create shopping list");
            return null;
        }
    }

    public async Task<bool> UpdateShoppingListAsync(Guid id, string name, CancellationToken ct = default)
    {
        try
        {
            var resp = await http.PutAsJsonAsync($"/api/shoppinglists/{id}", new { name }, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update shopping list {Id}", id);
            return false;
        }
    }

    public async Task<bool> DeleteShoppingListAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var resp = await http.DeleteAsync($"/api/shoppinglists/{id}", ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete shopping list {Id}", id);
            return false;
        }
    }

    public async Task<ShoppingListItemDto?> AddItemToListAsync(
        Guid listId, Guid productId, int quantity, CancellationToken ct = default)
    {
        try
        {
            var resp = await http.PostAsJsonAsync($"/api/shoppinglists/{listId}/items",
                new { productId, quantity }, ct);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<ShoppingListItemDto>(JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to add item to list {ListId}", listId);
            return null;
        }
    }

    public async Task<bool> RemoveItemFromListAsync(Guid listId, Guid itemId, CancellationToken ct = default)
    {
        try
        {
            var resp = await http.DeleteAsync($"/api/shoppinglists/{listId}/items/{itemId}", ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to remove item {ItemId} from list {ListId}", itemId, listId);
            return false;
        }
    }

    // ===== Optimization =====

    public async Task<OptimizationResultDto?> OptimizeAsync(
        Guid listId, string mode = "cheapest-total", string? postalCode = null,
        double radiusKm = 15, decimal threshold = 2.00m, CancellationToken ct = default)
    {
        try
        {
            var qs = new List<string> { $"mode={Uri.EscapeDataString(mode)}", $"radiusKm={radiusKm}", $"threshold={threshold}" };
            if (!string.IsNullOrWhiteSpace(postalCode)) qs.Add($"postalCode={Uri.EscapeDataString(postalCode)}");
            var url = $"/api/shoppinglists/{listId}/optimize?{string.Join('&', qs)}";
            return await http.GetFromJsonAsync<OptimizationResultDto>(url, JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to optimize list {ListId}", listId);
            return null;
        }
    }

    public async Task<ComparisonMatrixDto?> CompareAsync(
        Guid listId, string? postalCode = null, double radiusKm = 15, CancellationToken ct = default)
    {
        try
        {
            var qs = new List<string> { "mode=compare", $"radiusKm={radiusKm}" };
            if (!string.IsNullOrWhiteSpace(postalCode)) qs.Add($"postalCode={Uri.EscapeDataString(postalCode)}");
            var url = $"/api/shoppinglists/{listId}/optimize?{string.Join('&', qs)}";
            return await http.GetFromJsonAsync<ComparisonMatrixDto>(url, JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to compare stores for list {ListId}", listId);
            return null;
        }
    }

    // ===== Admin: Scraping =====

    public async Task<List<ScrapingStatusDto>> GetScrapingStatusAsync(CancellationToken ct = default)
    {
        try
        {
            return await http.GetFromJsonAsync<List<ScrapingStatusDto>>("/api/admin/scraping/status", JsonOptions, ct) ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get scraping status");
            return [];
        }
    }

    public async Task<ScrapingChainDetailDto?> GetScrapingChainDetailAsync(
        string chainSlug, CancellationToken ct = default)
    {
        try
        {
            return await http.GetFromJsonAsync<ScrapingChainDetailDto>(
                $"/api/admin/scraping/status/{Uri.EscapeDataString(chainSlug)}", JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get scraping detail for {ChainSlug}", chainSlug);
            return null;
        }
    }

    public async Task<(bool Success, string? Message)> TriggerScrapeAsync(
        string chainSlug, CancellationToken ct = default)
    {
        try
        {
            var resp = await http.PostAsync($"/api/admin/scraping/trigger/{Uri.EscapeDataString(chainSlug)}", null, ct);
            if (resp.IsSuccessStatusCode)
            {
                var result = await resp.Content.ReadFromJsonAsync<TriggerResponse>(JsonOptions, ct);
                return (true, result?.Message ?? "Scrape triggered.");
            }
            var error = await resp.Content.ReadAsStringAsync(ct);
            return (false, error);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to trigger scrape for {ChainSlug}", chainSlug);
            return (false, "An error occurred while triggering the scrape.");
        }
    }

    // ===== Admin: Mapping =====

    public async Task<MappingStatsDto?> GetMappingStatsAsync(CancellationToken ct = default)
    {
        try
        {
            return await http.GetFromJsonAsync<MappingStatsDto>("/api/admin/mapping/stats", JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get mapping stats");
            return null;
        }
    }

    public async Task<UncategorizedProductsResponse?> GetUncategorizedProductsAsync(
        int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        try
        {
            return await http.GetFromJsonAsync<UncategorizedProductsResponse>(
                $"/api/admin/mapping/uncategorized-products?page={page}&pageSize={pageSize}", JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get uncategorized products");
            return null;
        }
    }

    public async Task<List<UnmappedCategoryDto>> GetUnmappedCategoriesAsync(CancellationToken ct = default)
    {
        try
        {
            return await http.GetFromJsonAsync<List<UnmappedCategoryDto>>(
                "/api/admin/mapping/unmapped-categories", JsonOptions, ct) ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get unmapped categories");
            return [];
        }
    }

    public async Task<AdminStoreProductsResponse?> GetAdminStoreProductsAsync(
        string? status = null, string? chainSlug = null, int page = 1, int pageSize = 20,
        CancellationToken ct = default)
    {
        try
        {
            var qs = $"page={page}&pageSize={pageSize}";
            if (status is not null) qs += $"&status={Uri.EscapeDataString(status)}";
            if (chainSlug is not null) qs += $"&chainSlug={Uri.EscapeDataString(chainSlug)}";
            return await http.GetFromJsonAsync<AdminStoreProductsResponse>(
                $"/api/admin/mapping/store-products?{qs}", JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get admin store products");
            return null;
        }
    }

    public async Task<BackfillCategoriesResponse?> BackfillCategoriesAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await http.PostAsync("/api/admin/mapping/backfill-categories", null, ct);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<BackfillCategoriesResponse>(JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to backfill categories");
            return null;
        }
    }

    public async Task<RematchResponse?> RematchAsync(string? chainSlug = null, CancellationToken ct = default)
    {
        try
        {
            var url = "/api/admin/mapping/rematch";
            if (chainSlug is not null) url += $"?chainSlug={Uri.EscapeDataString(chainSlug)}";
            var resp = await http.PostAsync(url, null, ct);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<RematchResponse>(JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to rematch store products");
            return null;
        }
    }

    public async Task<(bool Success, string? Error)> AssignProductCategoryAsync(
        Guid productId, Guid categoryId, CancellationToken ct = default)
    {
        try
        {
            var resp = await http.PutAsJsonAsync(
                $"/api/admin/mapping/products/{productId}/category",
                new { categoryId }, ct);
            if (resp.IsSuccessStatusCode) return (true, null);
            var error = await resp.Content.ReadAsStringAsync(ct);
            return (false, error);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to assign category to product {ProductId}", productId);
            return (false, "An error occurred.");
        }
    }

    public async Task<(bool Success, string? Error)> AssignCanonicalProductAsync(
        Guid storeProductId, Guid canonicalProductId, CancellationToken ct = default)
    {
        try
        {
            var resp = await http.PutAsJsonAsync(
                $"/api/admin/mapping/store-products/{storeProductId}/canonical",
                new { canonicalProductId }, ct);
            if (resp.IsSuccessStatusCode) return (true, null);
            var error = await resp.Content.ReadAsStringAsync(ct);
            return (false, error);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to assign canonical product to store product {Id}", storeProductId);
            return (false, "An error occurred.");
        }
    }
}
