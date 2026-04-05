using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Savvori.WebApp.Services;
using Savvori.WebApp.Services.ApiModels;

namespace Savvori.WebApp.Pages.Admin.Mapping;

[Authorize(Roles = "admin")]
public class MappingIndexModel(SavvoriApiClient api) : PageModel
{
    // ── Tab selection ──────────────────────────────────────────────────────
    [BindProperty(SupportsGet = true)]
    public string Tab { get; set; } = "uncategorized";

    // ── Pagination ─────────────────────────────────────────────────────────
    [BindProperty(SupportsGet = true)]
    public new int Page { get; set; } = 1;

    // ── Store Products filter ──────────────────────────────────────────────
    [BindProperty(SupportsGet = true)]
    public string? StatusFilter { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ChainFilter { get; set; }

    // ── Data ───────────────────────────────────────────────────────────────
    public MappingStatsDto? Stats { get; set; }
    public UncategorizedProductsResponse? UncategorizedProducts { get; set; }
    public List<UnmappedCategoryDto> UnmappedCategories { get; set; } = [];
    public AdminStoreProductsResponse? StoreProducts { get; set; }
    public List<CategoryDto> AllCategories { get; set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (Page < 1) Page = 1;

        var statsTask = api.GetMappingStatsAsync(ct);
        var catsTask  = api.GetCategoriesAsync(ct);

        await Task.WhenAll(statsTask, catsTask);
        Stats = await statsTask;
        AllCategories = await catsTask;

        switch (Tab)
        {
            case "unmapped-categories":
                UnmappedCategories = await api.GetUnmappedCategoriesAsync(ct);
                break;

            case "store-products":
                StoreProducts = await api.GetAdminStoreProductsAsync(StatusFilter, ChainFilter, Page, 20, ct);
                break;

            default: // "uncategorized"
                Tab = "uncategorized";
                UncategorizedProducts = await api.GetUncategorizedProductsAsync(Page, 20, ct);
                break;
        }
    }

    // ── HTMX partial refresh after action ─────────────────────────────────

    public async Task<IActionResult> OnPostBackfillAsync(CancellationToken ct)
    {
        var result = await api.BackfillCategoriesAsync(ct);
        if (result is not null)
            TempData["Success"] = $"Backfill complete: {result.Updated} products categorized, {result.Skipped} still unmapped.";
        else
            TempData["Error"] = "Backfill request failed.";

        return RedirectToPage(new { tab = "uncategorized" });
    }

    public async Task<IActionResult> OnPostRematchAsync(CancellationToken ct)
    {
        var result = await api.RematchAsync(ct: ct);
        if (result is not null)
            TempData["Success"] = $"Re-match complete: {result.Matched} newly matched, {result.Remaining} still unmatched.";
        else
            TempData["Error"] = "Re-match request failed.";

        return RedirectToPage(new { tab = "store-products" });
    }

    public async Task<IActionResult> OnPostAssignCategoryAsync(
        Guid productId, Guid categoryId, CancellationToken ct)
    {
        var (success, error) = await api.AssignProductCategoryAsync(productId, categoryId, ct);
        if (success)
            TempData["Success"] = "Category assigned.";
        else
            TempData["Error"] = error ?? "Failed to assign category.";

        return RedirectToPage(new { tab = "uncategorized", page = Page });
    }

    public async Task<IActionResult> OnPostAssignCanonicalAsync(
        Guid storeProductId, Guid canonicalProductId, CancellationToken ct)
    {
        var (success, error) = await api.AssignCanonicalProductAsync(storeProductId, canonicalProductId, ct);
        if (success)
            TempData["Success"] = "Canonical product linked.";
        else
            TempData["Error"] = error ?? "Failed to link canonical product.";

        return RedirectToPage(new { tab = "store-products", page = Page, statusFilter = StatusFilter, chainFilter = ChainFilter });
    }
}
