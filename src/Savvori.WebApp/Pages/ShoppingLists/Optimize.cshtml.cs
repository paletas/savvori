using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Savvori.WebApp.Services;
using Savvori.WebApp.Services.ApiModels;

namespace Savvori.WebApp.Pages.ShoppingLists;

[Authorize]
public class OptimizeModel(SavvoriApiClient api) : PageModel
{
    public Guid ListId { get; set; }
    public string? ListName { get; set; }
    public string Mode { get; set; } = "cheapest-total";
    public string? PostalCode { get; set; }
    public double RadiusKm { get; set; } = 15;
    public decimal Threshold { get; set; } = 2.00m;

    public OptimizationResultDto? Result { get; set; }
    public ComparisonMatrixDto? Matrix { get; set; }
    public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync(
        Guid id,
        string mode = "cheapest-total",
        string? postalCode = null,
        double radiusKm = 15,
        decimal threshold = 2.00m,
        CancellationToken ct = default)
    {
        ListId = id;
        Mode = mode;
        PostalCode = postalCode;
        RadiusKm = radiusKm;
        Threshold = threshold;

        var lists = await api.GetShoppingListsAsync(ct);
        var list = lists.FirstOrDefault(l => l.Id == id);
        if (list is null) return NotFound();
        ListName = list.Name;

        // Only run optimization if user clicked Apply (query string present)
        if (Request.Query.ContainsKey("mode"))
        {
            if (mode == "compare")
            {
                Matrix = await api.CompareAsync(id, postalCode, radiusKm, ct);
                if (Matrix is null)
                    Error = "Could not load comparison data. The list may be empty or no stores match your filters.";
            }
            else
            {
                Result = await api.OptimizeAsync(id, mode, postalCode, radiusKm, threshold, ct);
                if (Result is null)
                    Error = "Could not optimize list. The list may be empty or no stores match your filters.";
            }
        }

        return Page();
    }
}
