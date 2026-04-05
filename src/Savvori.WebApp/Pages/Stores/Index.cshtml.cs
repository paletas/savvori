using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Savvori.WebApp.Services;
using Savvori.WebApp.Services.ApiModels;

namespace Savvori.WebApp.Pages.Stores;

public partial class StoresIndexModel(SavvoriApiClient api) : PageModel
{
    [Microsoft.AspNetCore.Mvc.BindProperty(SupportsGet = true)]
    public string? PostalCode { get; set; }

    [Microsoft.AspNetCore.Mvc.BindProperty(SupportsGet = true)]
    public double RadiusKm { get; set; } = 15;

    public NearbyStoresResponse? Result { get; set; }
    public string? Error { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(PostalCode)) return;

        if (!PostalCodeRegex().IsMatch(PostalCode))
        {
            Error = $"'{PostalCode}' is not a valid Portuguese postal code format. Use XXXX-XXX (e.g. 1000-001).";
            return;
        }

        Result = await api.GetNearbyStoresAsync(PostalCode, RadiusKm, ct);
        if (Result is null)
            Error = $"Could not find stores near postal code '{PostalCode}'. The postal code may not be in our service area.";
    }

    [GeneratedRegex(@"^\d{4}-\d{3}$")]
    private static partial Regex PostalCodeRegex();
}
