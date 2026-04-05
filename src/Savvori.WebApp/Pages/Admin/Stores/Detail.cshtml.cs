using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Savvori.WebApp.Services;
using Savvori.WebApp.Services.ApiModels;

namespace Savvori.WebApp.Pages.Admin.Stores;

[Authorize(Roles = "admin")]
public class StoresDetailModel(SavvoriApiClient api) : PageModel
{
    public string ChainSlug { get; set; } = string.Empty;
    public StoreLocationsResponse? Locations { get; set; }

    public async Task OnGetAsync(string slug, CancellationToken ct)
    {
        ChainSlug = slug;
        Locations = await api.GetStoreLocationsAsync(slug, ct);
    }
}
