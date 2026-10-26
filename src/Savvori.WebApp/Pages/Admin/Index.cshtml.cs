using Microsoft.AspNetCore.Mvc.RazorPages;
using Savvori.WebApp.Services;
using Savvori.WebApp.Services.ApiModels;

namespace Savvori.WebApp.Pages.Admin;

public class AdminIndexModel(SavvoriApiClient api) : PageModel
{
    public List<ScrapingStatusDto> DegradedChains { get; set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        DegradedChains = (await api.GetScrapingStatusAsync(ct)).Where(j => j.LastStatus == 5).ToList();
    }
}
