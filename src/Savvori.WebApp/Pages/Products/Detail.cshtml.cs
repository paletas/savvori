using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Savvori.WebApp.Services;
using Savvori.WebApp.Services.ApiModels;

namespace Savvori.WebApp.Pages.Products;

public class ProductDetailPageModel(SavvoriApiClient api) : PageModel
{
    public ProductDetailDto? Product { get; set; }
    public List<ProductSummaryDto> Alternatives { get; set; } = [];
    public List<ShoppingListDto> ShoppingLists { get; set; } = [];
    public List<ProductAliasDto> Aliases { get; set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var productTask = api.GetProductAsync(id, ct);
        var alternativesTask = api.GetAlternativesAsync(id, ct);
        var listsTask = api.GetShoppingListsAsync(ct);
        var aliasesTask = api.GetProductAliasesAsync(id, ct);

        await Task.WhenAll(productTask, alternativesTask, listsTask, aliasesTask);

        Product = productTask.Result;
        if (Product is null) return NotFound();

        Alternatives = alternativesTask.Result;
        ShoppingLists = listsTask.Result;
        Aliases = aliasesTask.Result;
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAliasAsync(Guid productId, string language, string? keywords, CancellationToken ct)
    {
        if (await api.SetProductAliasAsync(productId, language, keywords ?? string.Empty, ct))
            TempData["Success"] = "Search names saved.";
        else
            TempData["Error"] = "Could not save the search names.";
        return RedirectToPage(new { id = productId });
    }

    public async Task<IActionResult> OnPostResetAliasAsync(Guid productId, string language, CancellationToken ct)
    {
        if (await api.ResetProductAliasAsync(productId, language, ct))
            TempData["Success"] = "Search names reset; they will be suggested again on the next scan.";
        else
            TempData["Error"] = "Could not reset the search names.";
        return RedirectToPage(new { id = productId });
    }

    public async Task<IActionResult> OnPostAddToListAsync(
        Guid listId, Guid productId, int quantity, CancellationToken ct)
    {
        if (quantity < 1) quantity = 1;
        await api.AddItemToListAsync(listId, productId, quantity, ct);
        TempData["Success"] = "Added to shopping list!";
        return RedirectToPage(new { id = productId });
    }
}
