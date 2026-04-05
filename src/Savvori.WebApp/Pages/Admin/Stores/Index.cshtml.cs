using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Savvori.WebApp.Services;
using Savvori.WebApp.Services.ApiModels;

namespace Savvori.WebApp.Pages.Admin.Stores;

[Authorize(Roles = "admin")]
public class StoresIndexModel(SavvoriApiClient api) : PageModel
{
    public List<StoreChainDto> Chains { get; set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Chains = await api.GetStoresAsync(ct: ct);
    }
}
