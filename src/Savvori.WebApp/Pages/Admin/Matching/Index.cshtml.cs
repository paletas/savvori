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

    [BindProperty(Name = "p", SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    /// <summary>Bulk tab: the minimum cosine a bulk apply takes (query parameter "min").</summary>
    [BindProperty(Name = "min", SupportsGet = true)]
    public double Min { get; set; } = 0.90;

    public MatchBulkPreviewDto? BulkPreview { get; set; }
    public List<BulkBatchDto> Batches { get; set; } = [];

    public MatchingSummaryDto? Summary { get; set; }
    public ReviewPageDto? Review { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (PageNumber < 1) PageNumber = 1;
        if (Filter == "bulk")
        {
            var preview = api.GetMatchBulkPreviewAsync(Min, ct);
            var batches = api.GetBulkBatchesAsync("matching", ct);
            var sum = api.GetMatchingSummaryAsync(ct);
            await Task.WhenAll(preview, batches, sum);
            BulkPreview = await preview;
            Batches = await batches;
            Summary = await sum;
            return;
        }

        var summary = api.GetMatchingSummaryAsync(ct);
        var review = api.GetMatchingReviewAsync(Filter, PageNumber, ct);
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

    public async Task<IActionResult> OnPostBulkApplyAsync(double min, CancellationToken ct)
    {
        var (success, error) = await api.StartBulkApplyAsync("matching", min, ct);
        if (success) TempData["Success"] = "Bulk apply started. It runs in the background; refresh to see progress.";
        else TempData["Error"] = error ?? "The bulk run could not start.";
        return RedirectToPage(new { filter = "bulk", min });
    }

    public async Task<IActionResult> OnPostBulkUndoAsync(Guid id, double min, CancellationToken ct)
    {
        var (success, error) = await api.UndoBulkBatchAsync("matching", id, ct);
        if (success) TempData["Success"] = "Undo started. The pairs go back to the review queue.";
        else TempData["Error"] = error ?? "The undo could not start.";
        return RedirectToPage(new { filter = "bulk", min });
    }

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
        return RedirectToPage(new { filter = Filter, p = PageNumber });
    }
}
