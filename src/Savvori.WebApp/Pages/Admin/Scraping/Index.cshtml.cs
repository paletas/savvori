using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Savvori.WebApp.Services;
using Savvori.WebApp.Services.ApiModels;

namespace Savvori.WebApp.Pages.Admin.Scraping;

[Authorize(Roles = "admin")]
public class ScrapingIndexModel(SavvoriApiClient api) : PageModel
{
    public List<ScrapingStatusDto> Jobs { get; set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Jobs = await api.GetScrapingStatusAsync(ct);
    }

    public async Task<IActionResult> OnGetRefreshAsync(CancellationToken ct)
    {
        var jobs = await api.GetScrapingStatusAsync(ct);
        return Partial("_ScrapingStatusTable", jobs);
    }
}
