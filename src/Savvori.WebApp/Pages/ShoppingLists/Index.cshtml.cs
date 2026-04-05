using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Savvori.WebApp.Services;
using Savvori.WebApp.Services.ApiModels;

namespace Savvori.WebApp.Pages.ShoppingLists;

[Authorize]
public class ShoppingListsIndexModel(SavvoriApiClient api) : PageModel
{
    public List<ShoppingListDto> Lists { get; set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Lists = await api.GetShoppingListsAsync(ct);
    }

    public async Task<IActionResult> OnPostCreateAsync(string name, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            var list = await api.CreateShoppingListAsync(name.Trim(), ct);
            if (list is not null)
                return RedirectToPage("/ShoppingLists/Detail", new { id = list.Id });
        }
        TempData["Error"] = "Could not create list.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        await api.DeleteShoppingListAsync(id, ct);
        TempData["Success"] = "List deleted.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRenameAsync(Guid id, string name, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(name))
            await api.UpdateShoppingListAsync(id, name.Trim(), ct);
        return RedirectToPage();
    }
}
