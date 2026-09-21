using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Savvori.WebApp.Services;
using Savvori.WebApp.Services.ApiModels;

namespace Savvori.WebApp.Pages.Admin.Categorisation;

public class CategorisationIndexModel(SavvoriApiClient api) : PageModel
{
    /// <summary>suggested | applied</summary>
    [BindProperty(SupportsGet = true)]
    public string Filter { get; set; } = "suggested";

    [BindProperty(Name = "p", SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public CategorisationSummaryDto? Summary { get; set; }
    public CategorySuggestionPageDto? Suggestions { get; set; }
    public List<CategoryStringProposalDto> Strings { get; set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (PageNumber < 1) PageNumber = 1;
        var summary = api.GetCategorisationSummaryAsync(ct);
        var suggestions = api.GetCategorySuggestionsAsync(Filter, PageNumber, ct);
        var strings = api.GetCategoryStringProposalsAsync(ct);
        await Task.WhenAll(summary, suggestions, strings);
        Summary = await summary;
        Suggestions = await suggestions;
        Strings = await strings;
    }

    public Task<IActionResult> OnPostAcceptAsync(Guid id, CancellationToken ct) =>
        ActAsync("suggestions", id, "accept", "Category assigned.", ct);

    public Task<IActionResult> OnPostRejectAsync(Guid id, CancellationToken ct) =>
        ActAsync("suggestions", id, "reject", "Rejected: this product will not be proposed again.", ct);

    public Task<IActionResult> OnPostUndoAsync(Guid id, CancellationToken ct) =>
        ActAsync("suggestions", id, "undo", "Category removed again.", ct);

    public Task<IActionResult> OnPostAcceptStringAsync(Guid id, CancellationToken ct) =>
        ActAsync("strings", id, "accept", "Category assigned to every uncategorised product with that store category.", ct);

    public Task<IActionResult> OnPostRejectStringAsync(Guid id, CancellationToken ct) =>
        ActAsync("strings", id, "reject", "Rejected: products with that store category are decided one by one.", ct);

    public async Task<IActionResult> OnPostRunAsync(CancellationToken ct)
    {
        var r = await api.RunClassifierAsync(ct);
        if (r is null) TempData["Error"] = "The classifier run failed.";
        else if (r.SkippedReason is not null) TempData["Error"] = $"Classifier did not run: {r.SkippedReason}";
        else
            TempData["Success"] =
                $"{(r.DryRun ? "Dry run" : "Run")}: {r.Targets} uncategorised products, " +
                $"{(r.DryRun ? $"{r.WouldAssign} would be assigned" : $"{r.AutoAssigned} assigned")}, " +
                $"{r.ToReview} to review, {r.NoSuggestion} without a confident answer, {r.StringsDecided} store categories decided as a whole.";
        return RedirectToPage(new { filter = Filter });
    }

    private async Task<IActionResult> ActAsync(string kind, Guid id, string action, string ok, CancellationToken ct)
    {
        var (success, error) = await api.CategorisationActionAsync(kind, id, action, ct);
        if (success) TempData["Success"] = ok;
        else TempData["Error"] = error ?? "The action failed.";
        return RedirectToPage(new { filter = Filter, p = PageNumber });
    }
}
