using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Savvori.WebApp.Services.ApiModels;

namespace Savvori.WebApp.Services;

public class SavvoriApiClient(HttpClient http, ILogger<SavvoriApiClient> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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

    /// <summary>Adds <paramref name="quantity"/> more of a product to the list — increments if it's
    /// already there, rather than the API's strict create-only POST (which now 409s on a duplicate
    /// product; see ShoppingListsController.AddItem). Not atomic against a concurrent add of the
    /// same product from another tab, same as before this method existed.</summary>
    public async Task<ShoppingListItemDto?> AddItemToListAsync(
        Guid listId, Guid productId, int quantity, CancellationToken ct = default)
    {
        try
        {
            var lists = await GetShoppingListsAsync(ct);
            var existingQuantity = lists.FirstOrDefault(l => l.Id == listId)
                ?.Items.FirstOrDefault(i => i.ProductId == productId)?.Quantity ?? 0;

            var resp = await http.PutAsJsonAsync($"/api/shoppinglists/{listId}/items/{productId}",
                new { quantity = existingQuantity + quantity }, ct);
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

    public async Task<ModelStatusDto?> GetModelStatusAsync(CancellationToken ct = default)
    {
        try
        {
            return await http.GetFromJsonAsync<ModelStatusDto>("/api/admin/model/status", JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get model status");
            return null;
        }
    }

    public async Task<MatchReportDto?> GetMatchReportAsync(CancellationToken ct = default)
    {
        try
        {
            return await http.GetFromJsonAsync<MatchReportDto>("/api/admin/mapping/match-report", JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get match report");
            return null;
        }
    }

    public async Task<RecomputeSizesResponse?> RecomputeSizesAsync(bool dryRun = false, CancellationToken ct = default)
    {
        try
        {
            var resp = await http.PostAsync($"/api/admin/mapping/recompute-sizes?dryRun={dryRun.ToString().ToLowerInvariant()}", null, ct);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<RecomputeSizesResponse>(JsonOptions, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to recompute sizes");
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

    // ===== Admin: Matching review queue =====

    public async Task<MatchingSummaryDto?> GetMatchingSummaryAsync(CancellationToken ct = default)
    {
        try { return await http.GetFromJsonAsync<MatchingSummaryDto>("/api/admin/matching/summary", JsonOptions, ct); }
        catch (Exception ex) { logger.LogError(ex, "Failed to get matching summary"); return null; }
    }

    public async Task<ReviewPageDto?> GetMatchingReviewAsync(string filter, int page, CancellationToken ct = default)
    {
        try
        {
            return await http.GetFromJsonAsync<ReviewPageDto>(
                $"/api/admin/matching/review?filter={Uri.EscapeDataString(filter)}&page={page}&pageSize=10", JsonOptions, ct);
        }
        catch (Exception ex) { logger.LogError(ex, "Failed to get matching review queue"); return null; }
    }

    /// <summary>action: accept | reject | different-variant | undo</summary>
    public async Task<(bool Success, string? Error)> MatchingActionAsync(
        Guid id, string action, bool force = false, CancellationToken ct = default)
    {
        try
        {
            var resp = await http.PostAsync(
                $"/api/admin/matching/candidates/{id}/{action}?force={force.ToString().ToLowerInvariant()}", null, ct);
            if (resp.IsSuccessStatusCode) return (true, null);
            var body = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(JsonOptions, ct);
            return (false, body.TryGetProperty("message", out var m) ? m.GetString() : "The action failed.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Matching action {Action} failed", action);
            return (false, "The action failed.");
        }
    }

    public async Task<MatchingRunDto?> RunMatchingAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await http.PostAsync("/api/admin/matching/run", null, ct);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<MatchingRunDto>(JsonOptions, ct);
        }
        catch (Exception ex) { logger.LogError(ex, "Failed to run matching"); return null; }
    }

    // ===== Admin: model-suggested categories =====

    public async Task<CategorisationSummaryDto?> GetCategorisationSummaryAsync(CancellationToken ct = default)
    {
        try { return await http.GetFromJsonAsync<CategorisationSummaryDto>("/api/admin/categorisation/summary", JsonOptions, ct); }
        catch (Exception ex) { logger.LogError(ex, "Failed to get categorisation summary"); return null; }
    }

    public async Task<CategorySuggestionPageDto?> GetCategorySuggestionsAsync(string filter, int page, CancellationToken ct = default)
    {
        try
        {
            return await http.GetFromJsonAsync<CategorySuggestionPageDto>(
                $"/api/admin/categorisation/review?filter={Uri.EscapeDataString(filter)}&page={page}", JsonOptions, ct);
        }
        catch (Exception ex) { logger.LogError(ex, "Failed to get category suggestions"); return null; }
    }

    public async Task<List<CategoryStringProposalDto>> GetCategoryStringProposalsAsync(CancellationToken ct = default)
    {
        try { return await http.GetFromJsonAsync<List<CategoryStringProposalDto>>("/api/admin/categorisation/strings", JsonOptions, ct) ?? []; }
        catch (Exception ex) { logger.LogError(ex, "Failed to get category string proposals"); return []; }
    }

    /// <summary>kind: suggestions | strings; action: accept | reject | undo</summary>
    public async Task<(bool Success, string? Error)> CategorisationActionAsync(
        string kind, Guid id, string action, CancellationToken ct = default)
    {
        try
        {
            var resp = await http.PostAsync($"/api/admin/categorisation/{kind}/{id}/{action}", null, ct);
            if (resp.IsSuccessStatusCode) return (true, null);
            var body = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(JsonOptions, ct);
            return (false, body.TryGetProperty("message", out var m) ? m.GetString() : "The action failed.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Categorisation action {Kind}/{Action} failed", kind, action);
            return (false, "The action failed.");
        }
    }

    public async Task<ClassifierRunDto?> RunClassifierAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await http.PostAsync("/api/admin/categorisation/run", null, ct);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<ClassifierRunDto>(JsonOptions, ct);
        }
        catch (Exception ex) { logger.LogError(ex, "Failed to run classifier"); return null; }
    }

    // ===== Admin: bulk review =====

    private static string Inv(double value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public async Task<MatchBulkPreviewDto?> GetMatchBulkPreviewAsync(double minCosine, double? exactFloor = null, CancellationToken ct = default)
    {
        try { return await http.GetFromJsonAsync<MatchBulkPreviewDto>($"/api/admin/matching/bulk/preview?minCosine={Inv(minCosine)}&sample=30{(exactFloor is { } f ? $"&exactFloor={Inv(f)}" : "")}", JsonOptions, ct); }
        catch (Exception ex) { logger.LogError(ex, "Failed to get match bulk preview"); return null; }
    }

    public async Task<CategoryBulkPreviewDto?> GetCategoryBulkPreviewAsync(double minConfidence, CancellationToken ct = default)
    {
        try { return await http.GetFromJsonAsync<CategoryBulkPreviewDto>($"/api/admin/categorisation/bulk/preview?minConfidence={Inv(minConfidence)}&sample=30", JsonOptions, ct); }
        catch (Exception ex) { logger.LogError(ex, "Failed to get category bulk preview"); return null; }
    }

    /// <summary>area: matching | categorisation. Starts a background run.</summary>
    public async Task<(bool Success, string? Error)> StartBulkApplyAsync(string area, double threshold, CancellationToken ct = default, double? exactFloor = null)
    {
        try
        {
            var name = area == "matching" ? "minCosine" : "minConfidence";
            var resp = await http.PostAsync($"/api/admin/{area}/bulk/apply?{name}={Inv(threshold)}{(exactFloor is { } f ? $"&exactFloor={Inv(f)}" : "")}", null, ct);
            if (resp.IsSuccessStatusCode) return (true, null);
            var body = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(JsonOptions, ct);
            return (false, body.TryGetProperty("message", out var m) ? m.GetString() : "The bulk run could not start.");
        }
        catch (Exception ex) { logger.LogError(ex, "Bulk apply failed to start"); return (false, "The bulk run could not start."); }
    }

    public async Task<List<BulkBatchDto>> GetBulkBatchesAsync(string area, CancellationToken ct = default)
    {
        try { return await http.GetFromJsonAsync<List<BulkBatchDto>>($"/api/admin/{area}/bulk/batches", JsonOptions, ct) ?? []; }
        catch (Exception ex) { logger.LogError(ex, "Failed to get bulk batches"); return []; }
    }

    public async Task<(bool Success, string? Error)> UndoBulkBatchAsync(string area, Guid id, CancellationToken ct = default)
    {
        try
        {
            var resp = await http.PostAsync($"/api/admin/{area}/bulk/batches/{id}/undo", null, ct);
            if (resp.IsSuccessStatusCode) return (true, null);
            var body = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(JsonOptions, ct);
            return (false, body.TryGetProperty("message", out var m) ? m.GetString() : "The undo could not start.");
        }
        catch (Exception ex) { logger.LogError(ex, "Bulk undo failed to start"); return (false, "The undo could not start."); }
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

    // ===== Search aliases (per-language names used by product search) =====

    public async Task<List<ProductAliasDto>> GetProductAliasesAsync(Guid productId, CancellationToken ct = default)
    {
        try
        {
            return await http.GetFromJsonAsync<List<ProductAliasDto>>($"/api/products/{productId}/aliases", JsonOptions, ct) ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get search aliases for {ProductId}", productId);
            return [];
        }
    }

    public async Task<bool> SetProductAliasAsync(Guid productId, string language, string keywords, CancellationToken ct = default)
    {
        try
        {
            var resp = await http.PutAsJsonAsync($"/api/products/{productId}/aliases/{language}", new { keywords }, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set {Language} alias for {ProductId}", language, productId);
            return false;
        }
    }

    public async Task<bool> ResetProductAliasAsync(Guid productId, string language, CancellationToken ct = default)
    {
        try
        {
            var resp = await http.DeleteAsync($"/api/products/{productId}/aliases/{language}", ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reset {Language} alias for {ProductId}", language, productId);
            return false;
        }
    }
}
