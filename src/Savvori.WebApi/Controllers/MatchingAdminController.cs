using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Savvori.Shared;
using Savvori.WebApi.Modeling;

namespace Savvori.WebApi.Controllers;

/// <summary>
/// Admin API for the match review queue. None of these calls talks to the model: the review queue reads stored
/// candidates, human decisions are applied directly, and "run" only evaluates stored scores and queues judge jobs.
/// </summary>
[ApiController]
[Route("api/admin/matching")]
public class MatchingAdminController(
    SavvoriDbContext db, MatchApplier applier, MatchingService matching, TimeProvider time,
    Microsoft.Extensions.Options.IOptions<ModelOptions> options,
    MatchBulkService bulk, BulkRunner runner) : ControllerBase
{
    /// <summary>GET /api/admin/matching/summary — counts by candidate status and the cross-chain match effect.</summary>
    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken ct = default)
    {
        var byStatus = await db.MatchCandidates.GroupBy(c => c.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(ct);
        var byMethod = await db.MatchCandidates.Where(c => c.Method != null && c.Status == CandidateStatus.Applied)
            .GroupBy(c => c.Method!).Select(g => new { Method = g.Key, Count = g.Count() }).ToListAsync(ct);
        return Ok(new
        {
            ByStatus = byStatus.OrderBy(x => x.Status).Select(x => new { Status = x.Status.ToString(), x.Count }),
            AppliedByMethod = byMethod,
            MultiChainCanonicals = await MultiChainCanonicalsAsync(db, ct)
        });
    }

    /// <summary>
    /// GET /api/admin/matching/diagnostics/gap-words?minCosine=&amp;maxCosine=&amp;limit=2000 — for reviewing a cosine band
    /// without reading full product names: for every NeedsReview pair in range that VariantGuard neither flags as a
    /// conflict nor treats as identical (one side has a word the other lacks entirely — the "addition" pattern, the
    /// one shape VariantGuard's marker list can miss), aggregates that leftover word by how often it appears, with a
    /// couple of example pairs. A word with a high count and clearly food/flavour-shaped is a `Markers` candidate;
    /// changes nothing.
    /// </summary>
    [HttpGet("diagnostics/gap-words")]
    public async Task<IActionResult> GapWords(double minCosine = 0.80, double maxCosine = 0.90, int limit = 3000, CancellationToken ct = default)
    {
        var candidates = await db.MatchCandidates
            .Where(c => c.Status == CandidateStatus.NeedsReview && c.Cosine >= minCosine && c.Cosine < maxCosine &&
                        c.BrandCheck == CandidateBrandCheck.Ok)
            .OrderByDescending(c => c.Cosine).Take(limit)
            .Select(c => new { c.Id, c.Cosine, c.StoreProductAId, c.StoreProductBId }).ToListAsync(ct);

        var ids = candidates.SelectMany(c => new[] { c.StoreProductAId, c.StoreProductBId }).Distinct().ToList();
        var listings = new Dictionary<Guid, (string Name, string? Brand)>();
        foreach (var chunk in ids.Chunk(500))
            foreach (var p in await db.StoreProducts.AsNoTracking().Where(sp => chunk.Contains(sp.Id))
                         .Select(sp => new { sp.Id, sp.Name, sp.Brand }).ToListAsync(ct))
                listings[p.Id] = (p.Name, p.Brand);

        var words = new Dictionary<string, (int Count, List<object> Examples)>();
        foreach (var c in candidates)
        {
            if (!listings.TryGetValue(c.StoreProductAId, out var a) || !listings.TryGetValue(c.StoreProductBId, out var b)) continue;
            var v = VariantGuard.Compare(a.Name, a.Brand, b.Name, b.Brand);
            if (v.Conflict || v.Identical) continue; // already handled either way
            var extra = (v.OnlyA?.Count > 0 ? v.OnlyA : v.OnlyB) ?? [];
            if (extra.Count == 0) continue; // both sides had leftover words but neither is a marker - not this pattern
            foreach (var w in extra)
            {
                if (!words.TryGetValue(w, out var entry)) entry = (0, []);
                entry.Count++;
                if (entry.Examples.Count < 3) entry.Examples.Add(new { c.Id, c.Cosine, NameA = a.Name, NameB = b.Name });
                words[w] = entry;
            }
        }
        return Ok(new
        {
            MinCosine = minCosine, MaxCosine = maxCosine, CandidatesScanned = candidates.Count,
            Words = words.OrderByDescending(kv => kv.Value.Count)
                .Select(kv => new { Word = kv.Key, kv.Value.Count, kv.Value.Examples })
        });
    }

    /// <summary>Canonical products that have active prices from at least two different chains.</summary>
    public static async Task<int> MultiChainCanonicalsAsync(SavvoriDbContext db, CancellationToken ct)
    {
        var pairs = await db.StoreProducts.Where(sp => sp.IsActive && sp.CanonicalProductId != null)
            .Select(sp => new { sp.CanonicalProductId, sp.StoreChainId }).Distinct().ToListAsync(ct);
        return pairs.GroupBy(p => p.CanonicalProductId).Count(g => g.Count() > 1);
    }

    /// <summary>
    /// GET /api/admin/matching/review?filter=all|suggested|blocked|judge&amp;page=1&amp;pageSize=10
    /// Candidates needing a human, best first: suggestions from a dry run and judge "yes" first.
    /// </summary>
    [HttpGet("review")]
    public async Task<IActionResult> Review(
        string filter = "all", int page = 1, int pageSize = 10, CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 50) pageSize = 10;

        var wanted = filter == "applied" ? CandidateStatus.Applied : CandidateStatus.NeedsReview;
        var q = db.MatchCandidates.Where(c => c.Status == wanted);
        q = filter switch
        {
            "suggested" => q.Where(c => c.Suggestion != null),
            "blocked" => q.Where(c => c.Note != null),
            "judge" => q.Where(c => c.JudgeVerdict != null),
            _ => q
        };
        var total = await q.CountAsync(ct);
        var rows = await q
            .OrderByDescending(c => c.DecidedAt).ThenByDescending(c => c.Suggestion != null).ThenByDescending(c => c.Cosine)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(c => new
            {
                c.Id, c.Cosine, c.SizeKnown, BrandCheck = c.BrandCheck.ToString(), Status = c.Status.ToString(),
                c.Suggestion, Verdict = c.JudgeVerdict == null ? null : c.JudgeVerdict.ToString(), c.Note, c.Method,
                A = c.StoreProductAId, B = c.StoreProductBId
            }).ToListAsync(ct);

        var items = new List<object>();
        foreach (var r in rows)
        {
            items.Add(new
            {
                r.Id, r.Cosine, r.SizeKnown, r.BrandCheck, r.Status, r.Suggestion, r.Verdict, r.Note, r.Method,
                Warning = await applier.PreviewConflictAsync(r.A, r.B, ct),
                A = await ListingAsync(r.A, ct),
                B = await ListingAsync(r.B, ct)
            });
        }
        return Ok(new { Page = page, PageSize = pageSize, Total = total, TotalPages = (int)Math.Ceiling((double)total / pageSize), Items = items });
    }

    private async Task<object?> ListingAsync(Guid id, CancellationToken ct) =>
        await db.StoreProducts.Where(sp => sp.Id == id).Select(sp => new
        {
            sp.Id, sp.Name, sp.Brand, sp.SizeValue, Unit = sp.Unit.ToString(), sp.ImageUrl, sp.SourceUrl,
            Chain = sp.StoreChain.Name, sp.CanonicalProductId,
            Price = sp.Prices.Where(p => p.IsLatest).Select(p => (decimal?)p.Price).FirstOrDefault()
        }).FirstOrDefaultAsync(ct);

    // ---- bulk apply of the confident cosine suggestions -------------------------------------------------

    /// <summary>
    /// GET /api/admin/matching/bulk/preview?minCosine=0.90&amp;sample=30 - how many confident suggestions a bulk apply would
    /// take, and a random sample of them to spot-check first. Changes nothing.
    /// </summary>
    [HttpGet("bulk/preview")]
    public async Task<IActionResult> BulkPreview(double minCosine = 0.90, int sample = 30, double? exactFloor = null, CancellationToken ct = default)
    {
        sample = Math.Clamp(sample, 1, 100);
        var picks = await bulk.PickAsync(minCosine, exactFloor, ct);
        var ids = picks.OrderBy(_ => Random.Shared.Next()).Take(sample).Select(p => p.Id).ToList();
        var items = new List<object>();
        foreach (var id in ids)
        {
            var c = await db.MatchCandidates.AsNoTracking().FirstAsync(x => x.Id == id, ct);
            items.Add(new
            {
                c.Id, c.Cosine, c.SizeKnown, BrandCheck = c.BrandCheck.ToString(), Status = c.Status.ToString(), c.Suggestion,
                Verdict = (string?)null, Note = (string?)null, Method = (string?)null,
                Warning = await applier.PreviewConflictAsync(c.StoreProductAId, c.StoreProductBId, ct),
                A = await ListingAsync(c.StoreProductAId, ct), B = await ListingAsync(c.StoreProductBId, ct)
            });
        }
        return Ok(new
        {
            MinCosine = minCosine, ExactFloor = exactFloor, Eligible = picks.Count, EligibleExact = picks.Count(p => p.Exact),
            Sample = items, Busy = runner.IsBusy
        });
    }

    /// <summary>
    /// POST /api/admin/matching/bulk/apply?minCosine=0.90&amp;exactFloor=0.80 - applies every eligible suggestion as one
    /// undoable run (background). <c>exactFloor</c> also takes pairs down to that cosine whose names are identical.
    /// </summary>
    [HttpPost("bulk/apply")]
    public async Task<IActionResult> BulkApply(double minCosine = 0.90, double? exactFloor = null, CancellationToken ct = default)
    {
        if (runner.IsBusy) return Conflict(new { Message = "Another bulk run is in progress." });
        var batch = await bulk.CreateAsync(minCosine, exactFloor, ct);
        if (!runner.TryStart(batch.Id, sp => sp.GetRequiredService<MatchBulkService>().RunApplyAsync(batch.Id, exactFloor)))
        {
            db.BulkBatches.Remove(batch);
            await db.SaveChangesAsync(ct);
            return Conflict(new { Message = "Another bulk run is in progress." });
        }
        return Accepted(new { BatchId = batch.Id, batch.Total });
    }

    /// <summary>GET /api/admin/matching/bulk/batches - recent bulk runs with progress.</summary>
    [HttpGet("bulk/batches")]
    public async Task<IActionResult> BulkBatches(CancellationToken ct = default) =>
        Ok(await db.BulkBatches.Where(b => b.Kind == "matches").OrderByDescending(b => b.CreatedAt).Take(20)
            .Select(b => new { b.Id, b.Method, b.Threshold, Status = b.Status.ToString(), b.Total, b.Applied, b.Blocked, b.Undone, b.Error, b.CreatedAt, b.FinishedAt, b.UndoneAt })
            .ToListAsync(ct));

    /// <summary>POST /api/admin/matching/bulk/batches/{id}/undo - undoes every merge of a run (background); pairs return to the queue.</summary>
    [HttpPost("bulk/batches/{id:guid}/undo")]
    public async Task<IActionResult> BulkUndo(Guid id, CancellationToken ct = default)
    {
        var batch = await db.BulkBatches.FirstOrDefaultAsync(b => b.Id == id && b.Kind == "matches", ct);
        if (batch is null) return NotFound();
        if (batch.Status != BulkBatchStatus.Done) return Conflict(new { Message = "Only a finished run can be undone." });
        if (runner.IsBusy) return Conflict(new { Message = "Another bulk run is in progress." });
        batch.Status = BulkBatchStatus.Undoing;
        await db.SaveChangesAsync(ct);
        if (!runner.TryStart(batch.Id, sp => sp.GetRequiredService<MatchBulkService>().RunUndoAsync(batch.Id)))
        {
            batch.Status = BulkBatchStatus.Done;
            await db.SaveChangesAsync(ct);
            return Conflict(new { Message = "Another bulk run is in progress." });
        }
        return Accepted(new { BatchId = batch.Id });
    }

    /// <summary>POST /api/admin/matching/candidates/{id}/accept?force=false — a human decision, always wins.</summary>
    [HttpPost("candidates/{id:guid}/accept")]
    public async Task<IActionResult> Accept(Guid id, [FromQuery] bool force = false, CancellationToken ct = default)
    {
        var c = await db.MatchCandidates.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        if (c.Status == CandidateStatus.Applied) return Conflict(new { Message = "Already applied." });

        var result = await applier.ApplyAsync(c, MatchApplier.ManualMethod, manual: true, force, ct);
        if (!result.Succeeded)
            return Conflict(new { Message = result.Reason, CanForce = !force });
        return Ok(new { c.Id, Status = c.Status.ToString() });
    }

    /// <summary>POST /api/admin/matching/candidates/{id}/reject — never proposed again.</summary>
    [HttpPost("candidates/{id:guid}/reject")]
    public Task<IActionResult> Reject(Guid id, CancellationToken ct = default) =>
        Decide(id, CandidateStatus.Rejected, ct);

    /// <summary>POST /api/admin/matching/candidates/{id}/different-variant — never proposed again.</summary>
    [HttpPost("candidates/{id:guid}/different-variant")]
    public Task<IActionResult> DifferentVariant(Guid id, CancellationToken ct = default) =>
        Decide(id, CandidateStatus.DifferentVariant, ct);

    private async Task<IActionResult> Decide(Guid id, CandidateStatus status, CancellationToken ct)
    {
        var c = await db.MatchCandidates.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        if (c.Status == CandidateStatus.Applied)
            return Conflict(new { Message = "This match is applied; undo it first." });
        c.Status = status;
        c.Method = MatchApplier.ManualMethod;
        c.Suggestion = null;
        c.DecidedAt = time.GetUtcNow().UtcDateTime;
        c.Note = null;
        await db.SaveChangesAsync(ct);
        return Ok(new { c.Id, Status = c.Status.ToString() });
    }

    /// <summary>POST /api/admin/matching/candidates/{id}/undo — reverts an applied match and rejects the pair.</summary>
    [HttpPost("candidates/{id:guid}/undo")]
    public async Task<IActionResult> Undo(Guid id, CancellationToken ct = default)
    {
        var result = await applier.UndoAsync(id, ct);
        return result.Succeeded ? Ok(new { Id = id, Status = CandidateStatus.Rejected.ToString() })
                                : Conflict(new { Message = result.Reason });
    }

    /// <summary>
    /// POST /api/admin/matching/run — sorts stored candidates into the review queue now. Links nothing and makes no model call.
    /// </summary>
    [HttpPost("run")]
    public async Task<IActionResult> Run(CancellationToken ct = default) => Ok(await matching.RunAsync(ct));
}
