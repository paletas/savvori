using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Savvori.WebApp.Services;
using Savvori.WebApp.Services.ApiModels;

namespace Savvori.WebApp.Pages.Admin.Matching;

public class MatchingIndexModel(SavvoriApiClient api) : PageModel
{
    /// <summary>all | suggested | blocked | judge | applied</summary>
    [BindProperty(SupportsGet = true)]
    public string Filter { get; set; } = "all";

    [BindProperty(SupportsGet = true)]
    public new int Page { get; set; } = 1;

    public MatchingSummaryDto? Summary { get; set; }
    public ReviewPageDto? Review { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (Page < 1) Page = 1;
        var summary = api.GetMatchingSummaryAsync(ct);
        var review = api.GetMatchingReviewAsync(Filter, Page, ct);
        await Task.WhenAll(summary, review);
        Summary = await summary;
        Review = await review;
    }

    public Task<IActionResult> OnPostAcceptAsync(Guid id, bool force, CancellationToken ct) =>
        ActAsync(id, "accept", force, "Matched.", ct);

    public Task<IActionResult> OnPostRejectAsync(Guid id, CancellationToken ct) =>
        ActAsync(id, "reject", false, "Rejected: this pair will not be proposed again.", ct);

    public Task<IActionResult> OnPostVariantAsync(Guid id, CancellationToken ct) =>
        ActAsync(id, "different-variant", false, "Marked as a different variant: this pair will not be proposed again.", ct);

    public Task<IActionResult> OnPostUndoAsync(Guid id, CancellationToken ct) =>
        ActAsync(id, "undo", false, "Match undone and the pair rejected.", ct);

    public async Task<IActionResult> OnPostRunAsync(CancellationToken ct)
    {
        var r = await api.RunMatchingAsync(ct);
        if (r is null) TempData["Error"] = "The matching run failed.";
        else if (r.SkippedReason is not null) TempData["Error"] = $"Matching did not run: {r.SkippedReason}";
        else
            TempData["Success"] =
                $"{(r.DryRun ? "Dry run" : "Run")}: {r.Evaluated} candidates evaluated, " +
                $"{(r.DryRun ? $"{r.WouldAccept} would be accepted" : $"{r.AutoAccepted} accepted")}, " +
                $"{r.JudgeQueued} sent to the judge, {r.SentToReview} to review, {r.Blocked} blocked by safety rules.";
        return RedirectToPage(new { filter = Filter });
    }

    private async Task<IActionResult> ActAsync(Guid id, string action, bool force, string ok, CancellationToken ct)
    {
        var (success, error) = await api.MatchingActionAsync(id, action, force, ct);
        if (success) TempData["Success"] = ok;
        else TempData["Error"] = error ?? "The action failed.";
        return RedirectToPage(new { filter = Filter, page = Page });
    }
}
