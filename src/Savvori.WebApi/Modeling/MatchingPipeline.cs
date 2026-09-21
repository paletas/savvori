using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Quartz;
using Savvori.Shared;

namespace Savvori.WebApi.Modeling;

/// <summary>Builds the (product text only) judge input for a candidate and a hash that identifies exactly that input.</summary>
public static class JudgeInputs
{
    public static (JudgeItem A, JudgeItem B, string Hash) Build(
        StoreProduct a, StoreProduct b, string judgeModel)
    {
        var ia = Item(a);
        var ib = Item(b);
        var hash = EmbeddingFreshness.HashText($"{judgeModel}|{ia}|{ib}");
        return (ia, ib, hash);
    }

    private static JudgeItem Item(StoreProduct p) =>
        new(p.Name, p.Brand, p.SizeValue is > 0 ? $"{p.SizeValue:0.###} {p.Unit}" : null, p.StoreChain.Name);
}

public sealed record MatchingRunResult(
    string? SkippedReason, bool DryRun, int Evaluated, int AutoAccepted, int WouldAccept,
    int JudgeQueued, int SentToReview, int Blocked, int Left);

/// <summary>
/// Runs the matching tiers over stored candidates. It makes no model call itself: Tier B uses the stored cosine, Tier C
/// queues judge jobs. In degraded mode (feature off or breaker not closed) it decides nothing at all.
/// </summary>
public sealed class MatchingService(
    SavvoriDbContext db, MatchApplier applier, ModelJobQueue queue, ModelCircuitBreaker breaker,
    CurrentModelState state, IOptions<ModelOptions> options)
{
    public async Task<MatchingRunResult> RunAsync(CancellationToken ct = default)
    {
        var opts = options.Value;
        var o = opts.Matching;
        MatchingRunResult Skipped(string why) => new(why, o.DryRun, 0, 0, 0, 0, 0, 0, 0);
        if (!opts.Enabled) return Skipped("Model features are disabled.");
        if (!breaker.IsClosed) return Skipped("Model unavailable (degraded mode): nothing model-dependent is decided.");

        var info = state.Info;
        var eligible = await db.MatchCandidates
            .Where(c => c.ModelName == opts.EmbeddingModel && (info == null || c.ModelDigest == info.ModelDigest) &&
                        (c.Status == CandidateStatus.Proposed || c.Status == CandidateStatus.PendingJudge ||
                         (!o.DryRun && c.Status == CandidateStatus.NeedsReview &&
                          (c.Suggestion == "embedding-cosine" || (o.AutoApplyJudgeYes && c.Suggestion == "embedding-judge")))))
            .OrderByDescending(c => c.Cosine)
            .ToListAsync(ct);

        int autoAccepted = 0, wouldAccept = 0, review = 0, blocked = 0, left = 0;
        var toJudge = new List<MatchCandidate>();

        foreach (var c in eligible)
        {
            ct.ThrowIfCancellationRequested();
            // A dry-run judge "yes" that is now allowed to apply: no need to ask the judge again.
            if (!o.DryRun && o.AutoApplyJudgeYes && c.Suggestion == "embedding-judge" && c.JudgeVerdict == JudgeVerdict.Yes)
            {
                var r = await applier.ApplyAsync(c, "embedding-judge", manual: false, force: false, ct);
                if (r.Succeeded) autoAccepted++; else { Block(c, r); blocked++; }
                continue;
            }

            switch (MatchPolicy.Decide(c.Cosine, c.SizeKnown, c.BrandCheck, o))
            {
                case MatchTier.AutoAccept when o.DryRun:
                    c.Status = CandidateStatus.NeedsReview;
                    c.Suggestion = "embedding-cosine";
                    wouldAccept++;
                    break;
                case MatchTier.AutoAccept:
                    var applied = await applier.ApplyAsync(c, "embedding-cosine", manual: false, force: false, ct);
                    if (applied.Succeeded) autoAccepted++; else { Block(c, applied); blocked++; }
                    break;
                case MatchTier.Judge:
                    if (toJudge.Count < o.MaxJudgeJobsPerRun) toJudge.Add(c); else left++;
                    break;
                case MatchTier.Review:
                    c.Status = CandidateStatus.NeedsReview;
                    review++;
                    break;
                default:
                    left++;
                    break;
            }
        }
        await db.SaveChangesAsync(ct);

        var queued = await QueueJudgeJobsAsync(toJudge, opts.JudgeModel, ct);
        return new(null, o.DryRun, eligible.Count, autoAccepted, wouldAccept, queued, review, blocked, left);
    }

    private static void Block(MatchCandidate c, ApplyResult r)
    {
        c.Status = CandidateStatus.NeedsReview;
        c.Note = r.Reason;
    }

    private async Task<int> QueueJudgeJobsAsync(List<MatchCandidate> candidates, string judgeModel, CancellationToken ct)
    {
        if (candidates.Count == 0) return 0;
        var ids = candidates.SelectMany(c => new[] { c.StoreProductAId, c.StoreProductBId }).Distinct().ToList();
        var products = new Dictionary<Guid, StoreProduct>();
        foreach (var chunk in ids.Chunk(500))
            foreach (var p in await db.StoreProducts.AsNoTracking().Include(sp => sp.StoreChain)
                         .Where(sp => chunk.Contains(sp.Id)).ToListAsync(ct))
                products[p.Id] = p;

        var jobs = new List<(Guid, string)>();
        foreach (var c in candidates)
        {
            if (!products.TryGetValue(c.StoreProductAId, out var a) || !products.TryGetValue(c.StoreProductBId, out var b)) continue;
            c.Status = CandidateStatus.PendingJudge;
            jobs.Add((c.Id, JudgeInputs.Build(a, b, judgeModel).Hash));
        }
        await db.SaveChangesAsync(ct);
        return await queue.EnqueueManyAsync(ModelJobType.Judge, jobs, ct);
    }
}

/// <summary>
/// Tier C worker: asks the judge about one candidate pair. A failed or timed-out call throws, which leaves the
/// candidate PendingJudge and the job retried by the queue. It is never treated as a "no".
/// </summary>
public sealed class JudgeJobHandler(
    SavvoriDbContext db, IPairJudge judge, MatchApplier applier, IOptions<ModelOptions> options) : IModelJobHandler
{
    public ModelJobType Type => ModelJobType.Judge;

    public async Task HandleAsync(ModelJob job, CancellationToken ct)
    {
        var c = await db.MatchCandidates.FirstOrDefaultAsync(x => x.Id == job.SubjectId, ct);
        if (c is null || c.Status != CandidateStatus.PendingJudge) return; // decided or removed meanwhile

        var a = await db.StoreProducts.Include(sp => sp.StoreChain).FirstOrDefaultAsync(sp => sp.Id == c.StoreProductAId, ct);
        var b = await db.StoreProducts.Include(sp => sp.StoreChain).FirstOrDefaultAsync(sp => sp.Id == c.StoreProductBId, ct);
        if (a is null || b is null) return;
        var opts = options.Value;
        var (ia, ib, hash) = JudgeInputs.Build(a, b, opts.JudgeModel);
        if (hash != job.PayloadHash) return; // the listings changed since queueing; the next matching run re-queues

        var verdict = await judge.JudgeAsync(ia, ib, ct); // throws on failure: no decision, retried
        c.JudgeVerdict = verdict;
        c.JudgeModel = opts.JudgeModel;

        if (verdict == JudgeVerdict.Yes && (opts.Matching.DryRun || !opts.Matching.AutoApplyJudgeYes))
        {
            c.Status = CandidateStatus.NeedsReview;
            c.Suggestion = "embedding-judge";
        }
        else if (verdict == JudgeVerdict.Yes)
        {
            var r = await applier.ApplyAsync(c, "embedding-judge", manual: false, force: false, ct);
            if (!r.Succeeded) { c.Status = CandidateStatus.NeedsReview; c.Note = r.Reason; }
        }
        else
        {
            c.Status = CandidateStatus.NeedsReview; // "no" and "unclear" both go to a human, verdict shown as a hint
        }
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Nightly, after candidate generation: run the matching tiers. Skipped while the flag is off or in degraded mode.</summary>
[DisallowConcurrentExecution]
public sealed class MatchingJob(
    IOptions<ModelOptions> options, IServiceScopeFactory scopes, ILogger<MatchingJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        if (!options.Value.Enabled) return;
        using var scope = scopes.CreateScope();
        var r = await scope.ServiceProvider.GetRequiredService<MatchingService>().RunAsync(context.CancellationToken);
        logger.LogInformation(
            "Matching run (dryRun={DryRun}): skipped={Skipped}, {Evaluated} evaluated, {Auto} auto-accepted, " +
            "{Would} would accept, {Judge} judge jobs, {Review} to review, {Blocked} blocked, {Left} left.",
            r.DryRun, r.SkippedReason, r.Evaluated, r.AutoAccepted, r.WouldAccept, r.JudgeQueued, r.SentToReview, r.Blocked, r.Left);
    }
}
