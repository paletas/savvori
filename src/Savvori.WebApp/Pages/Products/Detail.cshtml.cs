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

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var productTask = api.GetProductAsync(id, ct);
        var alternativesTask = api.GetAlternativesAsync(id, ct);
        var listsTask = User.Identity?.IsAuthenticated == true
            ? api.GetShoppingListsAsync(ct)
            : Task.FromResult(new List<ShoppingListDto>());

        await Task.WhenAll(productTask, alternativesTask, listsTask);

        Product = productTask.Result;
        if (Product is null) return NotFound();

        Alternatives = alternativesTask.Result;
        ShoppingLists = listsTask.Result;
        return Page();
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
