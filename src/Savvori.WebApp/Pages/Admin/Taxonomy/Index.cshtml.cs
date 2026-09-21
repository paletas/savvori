using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Savvori.WebApp.Services;
using Savvori.WebApp.Services.ApiModels;

namespace Savvori.WebApp.Pages.Admin.Taxonomy;

public class TaxonomyIndexModel(SavvoriApiClient api) : PageModel
{
    public TaxonomyPlanDto? Plan { get; set; }

    public async Task OnGetAsync(CancellationToken ct) => Plan = await api.GetTaxonomyPlanAsync(ct);

    public async Task<IActionResult> OnPostApplyAsync(CancellationToken ct) =>
        await ActAsync("apply", "Taxonomy v2 applied. Products keep their old category so this can be reverted.", ct);

    public async Task<IActionResult> OnPostRevertAsync(CancellationToken ct) =>
        await ActAsync("revert", "Reverted: migrated products are back on their v1 categories.", ct);

    private async Task<IActionResult> ActAsync(string action, string ok, CancellationToken ct)
    {
        var (success, error) = await api.TaxonomyActionAsync(action, ct);
        if (success) TempData["Success"] = ok;
        else TempData["Error"] = error ?? "The action failed.";
        return RedirectToPage();
    }
}
