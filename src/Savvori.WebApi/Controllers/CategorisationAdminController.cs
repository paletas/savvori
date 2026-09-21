using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Savvori.Shared;
using Savvori.WebApi.Modeling;

namespace Savvori.WebApi.Controllers;

/// <summary>
/// Admin API for model-suggested categories. Human decisions are applied directly and never call the model;
/// "run" evaluates stored embeddings only.
/// </summary>
[ApiController]
[Route("api/admin/categorisation")]
public class CategorisationAdminController(
    SavvoriDbContext db, CategoryClassifier classifier, IOptions<ModelOptions> options,
    CategoryBulkService bulk, BulkRunner runner) : ControllerBase
{
    /// <summary>GET /api/admin/categorisation/summary</summary>
    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken ct = default)
    {
        var byStatus = await db.CategorySuggestions.GroupBy(s => s.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(ct);
        var strings = await db.CategoryStringDecisions.GroupBy(s => s.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(ct);
        return Ok(new
        {
            DryRun = options.Value.Categories.DryRun,
            Uncategorised = await db.Products.CountAsync(p => p.CategoryId == null, ct),
            ByStatus = byStatus.OrderBy(x => x.Status).Select(x => new { Status = x.Status.ToString(), x.Count }),
            StringsByStatus = strings.OrderBy(x => x.Status).Select(x => new { Status = x.Status.ToString(), x.Count })
        });
    }

    /// <summary>GET /api/admin/categorisation/review?filter=suggested|applied&amp;page=</summary>
    [HttpGet("review")]
    public async Task<IActionResult> Review(string filter = "suggested", int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 20;
        var wanted = filter == "applied" ? CategorySuggestionStatus.Applied : CategorySuggestionStatus.Suggested;
        var q = db.CategorySuggestions.Where(s => s.Status == wanted);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(s => s.Confidence).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(s => new
            {
                s.Id, s.ProductId, ProductName = s.Product.Name, s.Product.Brand, s.Product.ImageUrl, RawCategory = s.Product.Category,
                Suggested = s.SuggestedCategory.Name,
                RunnerUp = s.RunnerUpCategoryId == null ? null : db.ProductCategories.Where(c => c.Id == s.RunnerUpCategoryId).Select(c => c.Name).FirstOrDefault(),
                s.Confidence, s.NeighbourCount, Status = s.Status.ToString(), s.Method
            }).ToListAsync(ct);
        return Ok(new { Page = page, PageSize = pageSize, Total = total, TotalPages = (int)Math.Ceiling((double)total / pageSize), Items = items });
    }

    /// <summary>GET /api/admin/categorisation/strings — whole-string proposals waiting for a decision.</summary>
    [HttpGet("strings")]
    public async Task<IActionResult> Strings(CancellationToken ct = default) =>
        Ok(await db.CategoryStringDecisions.Where(d => d.Status == CategorySuggestionStatus.Suggested)
            .OrderByDescending(d => d.Support)
            .Select(d => new { d.Id, d.RawString, d.Support, d.Confidence, Category = d.Category!.Name }).ToListAsync(ct));

    // ---- bulk apply of the confident predictions ---------------------------------------------------------

    /// <summary>
    /// GET /api/admin/categorisation/bulk/preview?minConfidence=0.90&amp;sample=30 - how many predictions a bulk apply would
    /// take and a random sample to spot-check first. Changes nothing.
    /// </summary>
    [HttpGet("bulk/preview")]
    public async Task<IActionResult> BulkPreview(double minConfidence = 0.90, int sample = 30, CancellationToken ct = default)
    {
        sample = Math.Clamp(sample, 1, 100);
        var eligible = bulk.Eligible(minConfidence);
        var items = await eligible.OrderBy(_ => EF.Functions.Random()).Take(sample)
            .Select(s => new
            {
                s.Id, s.ProductId, ProductName = s.Product.Name, s.Product.Brand, s.Product.ImageUrl, RawCategory = s.Product.Category,
                Suggested = s.SuggestedCategory.Name, s.Confidence, s.NeighbourCount
            }).ToListAsync(ct);
        return Ok(new { MinConfidence = minConfidence, Eligible = await eligible.CountAsync(ct), Sample = items, Busy = runner.IsBusy });
    }

    /// <summary>POST /api/admin/categorisation/bulk/apply?minConfidence=0.90 - assigns every eligible prediction as one undoable run (background).</summary>
    [HttpPost("bulk/apply")]
    public async Task<IActionResult> BulkApply(double minConfidence = 0.90, CancellationToken ct = default)
    {
        if (runner.IsBusy) return Conflict(new { Message = "Another bulk run is in progress." });
        var batch = await bulk.CreateAsync(minConfidence, ct);
        if (!runner.TryStart(batch.Id, sp => sp.GetRequiredService<CategoryBulkService>().RunApplyAsync(batch.Id)))
        {
            db.BulkBatches.Remove(batch);
            await db.SaveChangesAsync(ct);
            return Conflict(new { Message = "Another bulk run is in progress." });
        }
        return Accepted(new { BatchId = batch.Id, batch.Total });
    }

    [HttpGet("bulk/batches")]
    public async Task<IActionResult> BulkBatches(CancellationToken ct = default) =>
        Ok(await db.BulkBatches.Where(b => b.Kind == "categories").OrderByDescending(b => b.CreatedAt).Take(20)
            .Select(b => new { b.Id, b.Method, b.Threshold, Status = b.Status.ToString(), b.Total, b.Applied, b.Blocked, b.Undone, b.Error, b.CreatedAt, b.FinishedAt, b.UndoneAt })
            .ToListAsync(ct));

    /// <summary>POST /api/admin/categorisation/bulk/batches/{id}/undo - removes the categories a run assigned; predictions return to the queue.</summary>
    [HttpPost("bulk/batches/{id:guid}/undo")]
    public async Task<IActionResult> BulkUndo(Guid id, CancellationToken ct = default)
    {
        var batch = await db.BulkBatches.FirstOrDefaultAsync(b => b.Id == id && b.Kind == "categories", ct);
        if (batch is null) return NotFound();
        if (batch.Status != BulkBatchStatus.Done) return Conflict(new { Message = "Only a finished run can be undone." });
        if (runner.IsBusy) return Conflict(new { Message = "Another bulk run is in progress." });
        batch.Status = BulkBatchStatus.Undoing;
        await db.SaveChangesAsync(ct);
        if (!runner.TryStart(batch.Id, sp => sp.GetRequiredService<CategoryBulkService>().RunUndoAsync(batch.Id)))
        {
            batch.Status = BulkBatchStatus.Done;
            await db.SaveChangesAsync(ct);
            return Conflict(new { Message = "Another bulk run is in progress." });
        }
        return Accepted(new { BatchId = batch.Id });
    }

    [HttpPost("suggestions/{id:guid}/accept")]
    public async Task<IActionResult> Accept(Guid id, CancellationToken ct = default) => Result(await classifier.AcceptAsync(id, ct));

    [HttpPost("suggestions/{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, CancellationToken ct = default) => Result(await classifier.RejectAsync(id, ct));

    [HttpPost("suggestions/{id:guid}/undo")]
    public async Task<IActionResult> Undo(Guid id, CancellationToken ct = default) => Result(await classifier.UndoAsync(id, ct));

    [HttpPost("strings/{id:guid}/accept")]
    public async Task<IActionResult> AcceptString(Guid id, CancellationToken ct = default)
    {
        var (ok, error, updated) = await classifier.AcceptStringAsync(id, ct);
        return ok ? Ok(new { Updated = updated }) : Conflict(new { Message = error });
    }

    [HttpPost("strings/{id:guid}/reject")]
    public async Task<IActionResult> RejectString(Guid id, CancellationToken ct = default) =>
        await classifier.RejectStringAsync(id, ct) ? Ok() : NotFound();

    /// <summary>POST /api/admin/categorisation/run — evaluate stored embeddings now (respects Model:Categories:DryRun).</summary>
    [HttpPost("run")]
    public async Task<IActionResult> Run(CancellationToken ct = default) => Ok(await classifier.RunAsync(ct));

    private IActionResult Result((bool Ok, string? Error) r) =>
        r.Ok ? Ok() : Conflict(new { Message = r.Error });
}
