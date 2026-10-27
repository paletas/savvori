using Microsoft.AspNetCore.Mvc.RazorPages;
using Savvori.WebApp.Services;
using Savvori.WebApp.Services.ApiModels;

namespace Savvori.WebApp.Pages.Admin;

public class AdminIndexModel(SavvoriApiClient api) : PageModel
{
    public List<ScrapingStatusDto> DegradedChains { get; set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        // 5 = Degraded; 3 = Failed, listed only when the run failed because category discovery failed.
        DegradedChains = (await api.GetScrapingStatusAsync(ct))
            .Where(j => j.LastStatus == 5
                        || (j.LastStatus == 3 && j.HealthReason?.StartsWith("Discovery failed", StringComparison.Ordinal) == true))
            .ToList();
    }
}
