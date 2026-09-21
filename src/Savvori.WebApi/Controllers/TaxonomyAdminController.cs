using Microsoft.AspNetCore.Mvc;
using Savvori.WebApi.Scraping;

namespace Savvori.WebApi.Controllers;

/// <summary>
/// Admin API for the taxonomy v1 -> v2 migration (docs/TAXONOMY_V2.md). The plan is a dry run; apply and revert are
/// explicit actions, nothing runs automatically.
/// </summary>
[ApiController]
[Route("api/admin/taxonomy")]
public class TaxonomyAdminController(TaxonomyMigrationService migration) : ControllerBase
{
    /// <summary>GET /api/admin/taxonomy/plan — what an apply would do, per v1 category. Changes nothing.</summary>
    [HttpGet("plan")]
    public async Task<IActionResult> Plan(CancellationToken ct = default) => Ok(await migration.PlanAsync(ct));

    /// <summary>POST /api/admin/taxonomy/apply — seeds v2, relabels products (v1 ids kept), sets tags.</summary>
    [HttpPost("apply")]
    public async Task<IActionResult> Apply(CancellationToken ct = default)
    {
        var r = await migration.ApplyAsync(ct);
        return r.Applied ? Ok(r) : Conflict(new { Message = r.Reason });
    }

    /// <summary>POST /api/admin/taxonomy/revert — puts migrated products back on their v1 categories.</summary>
    [HttpPost("revert")]
    public async Task<IActionResult> Revert(CancellationToken ct = default)
    {
        var (reverted, reason, restored) = await migration.RevertAsync(ct);
        return reverted ? Ok(new { Restored = restored }) : Conflict(new { Message = reason });
    }

    /// <summary>POST /api/admin/taxonomy/backfill-tags — adds missing bio / sem-lactose / sem-gluten / vegan / sem-acucar tags.</summary>
    [HttpPost("backfill-tags")]
    public async Task<IActionResult> BackfillTags(CancellationToken ct = default) =>
        Ok(new { Added = await migration.BackfillTagsAsync(ct) });
}
