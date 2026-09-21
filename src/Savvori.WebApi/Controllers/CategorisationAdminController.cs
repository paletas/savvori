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
    SavvoriDbContext db, CategoryClassifier classifier, IOptions<ModelOptions> options) : ControllerBase
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
        var q = db.CategorySuggestions.Where(s => s.Status == wanted && (wanted != CategorySuggestionStatus.Applied || s.Method != MatchApplier.ManualMethod));
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
