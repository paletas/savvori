using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Savvori.WebApp.Services;
using Savvori.WebApp.Services.ApiModels;

namespace Savvori.WebApp.Pages.Admin.Scraping;

[Authorize(Roles = "admin")]
public class ScrapingDetailModel(SavvoriApiClient api) : PageModel
{
    public string ChainSlug { get; set; } = string.Empty;
    public ScrapingChainDetailDto? Detail { get; set; }

    public async Task OnGetAsync(string slug, CancellationToken ct)
    {
        ChainSlug = slug;
        Detail = await api.GetScrapingChainDetailAsync(slug, ct);
    }

    public async Task<IActionResult> OnPostTriggerAsync(string slug, CancellationToken ct)
    {
        ChainSlug = slug;
        var (success, message) = await api.TriggerScrapeAsync(slug, ct);
        if (success)
            TempData["Success"] = message ?? $"Scrape triggered for '{slug}'.";
        else
            TempData["Error"] = message ?? "Failed to trigger scrape.";

        return RedirectToPage(new { slug });
    }
}
