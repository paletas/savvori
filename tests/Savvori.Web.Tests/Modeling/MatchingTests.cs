using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Savvori.Shared;
using Savvori.WebApi;
using Savvori.WebApi.Modeling;

namespace Savvori.Web.Tests.Modeling;

public sealed class MatchPolicyTests
{
    private static readonly ModelOptions.MatchingOptions O = new();

    [Theory]
    [InlineData(0.95, true, CandidateBrandCheck.Ok, MatchTier.Confident)]
    [InlineData(0.90, true, CandidateBrandCheck.Ok, MatchTier.Confident)]
    [InlineData(0.899, true, CandidateBrandCheck.Ok, MatchTier.Judge)]
    [InlineData(0.80, true, CandidateBrandCheck.Ok, MatchTier.Judge)]
    [InlineData(0.799, true, CandidateBrandCheck.Ok, MatchTier.Review)]
    [InlineData(0.70, true, CandidateBrandCheck.Ok, MatchTier.Review)]
    [InlineData(0.699, true, CandidateBrandCheck.Ok, MatchTier.Leave)]
    [InlineData(0.94, false, CandidateBrandCheck.Ok, MatchTier.Judge)]        // size unknown needs 0.95
    [InlineData(0.95, false, CandidateBrandCheck.Ok, MatchTier.Confident)]
    [InlineData(0.85, false, CandidateBrandCheck.Ok, MatchTier.Judge)]
    [InlineData(0.84, false, CandidateBrandCheck.Ok, MatchTier.Review)]
    [InlineData(0.99, true, CandidateBrandCheck.Unknown, MatchTier.Judge)]    // brand not confirmed: never cosine-only
    public void Decide_FollowsTheConfiguredTiers(double cosine, bool sizeKnown, CandidateBrandCheck brand, MatchTier expected) =>
        Assert.Equal(expected, MatchPolicy.Decide(cosine, sizeKnown, brand, O));

    [Fact]
    public void Decide_ThresholdsAreConfigurable()
    {
        var o = new ModelOptions.MatchingOptions { AcceptCosineSizeKnown = 0.97, AutoAcceptRequiresBrandOk = false };
        Assert.Equal(MatchTier.Judge, MatchPolicy.Decide(0.95, true, CandidateBrandCheck.Ok, o));
        Assert.Equal(MatchTier.Confident, MatchPolicy.Decide(0.98, true, CandidateBrandCheck.Unknown, o));
    }
}

public sealed class MatchingTests : IDisposable
{
    private readonly PipelineHost _h = new();
    public void Dispose() => _h.Dispose();

    private Task DrainAsync() => _h.DrainAsync();

    /// <summary>What a person does with the confident suggestions: one bulk apply at the default threshold.</summary>
    private async Task<BulkBatch> BulkApplyAsync(double min = 0.90)
    {
        using var scope = _h.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<MatchBulkService>();
        var ct = TestContext.Current.CancellationToken;
        var batch = await svc.CreateAsync(min, ct: ct);
        await svc.RunApplyAsync(batch.Id, ct: ct);
        return await scope.ServiceProvider.GetRequiredService<SavvoriDbContext>().BulkBatches.AsNoTracking().SingleAsync(b => b.Id == batch.Id, ct);
    }

    /// <summary>The nightly run, then a person bulk-applying its confident suggestions.</summary>
    private async Task<BulkBatch> RunThenBulkApplyAsync()
    {
        await _h.RunMatchingAsync();
        return await BulkApplyAsync();
    }

    [Fact]
    public async Task FeatureOff_DecidesNothing()
    {
        _h.Options.Enabled = false;
        var (a, _) = _h.AddListed(_h.ChainA, "Leite");
        var (b, _) = _h.AddListed(_h.ChainB, "Leite");
        var c = _h.AddCandidate(a, b, 0.97);

        var r = await _h.RunMatchingAsync();

        Assert.NotNull(r.SkippedReason);
        Assert.Equal(CandidateStatus.Proposed, _h.Candidate(c).Status);
    }

    [Fact]
    public async Task DegradedMode_BreakerOpen_NothingModelDependentIsAcceptedOrQueued()
    {
        var (a, ca) = _h.AddListed(_h.ChainA, "Leite");
        var (b, cb) = _h.AddListed(_h.ChainB, "Leite");
        var hi = _h.AddCandidate(a, b, 0.97);
        for (var i = 0; i < 3; i++) _h.Breaker.RecordFailure("down");

        var r = await _h.RunMatchingAsync();

        Assert.Contains("degraded", r.SkippedReason);
        Assert.Equal(CandidateStatus.Proposed, _h.Candidate(hi).Status);
        Assert.Equal(ca, _h.Sp(a).CanonicalProductId); // existing matches untouched
        Assert.Equal(cb, _h.Sp(b).CanonicalProductId);
        Assert.Empty(_h.Query(db => db.ModelJobs.ToList()));
    }

    [Fact]
    public async Task TheRun_NeverLinksAnything_ItOnlyQueuesSuggestions()
    {
        var (a, ca) = _h.AddListed(_h.ChainA, "Leite");
        var (b, cb) = _h.AddListed(_h.ChainB, "Leite");
        var c = _h.AddCandidate(a, b, 0.97);

        var r = await _h.RunMatchingAsync();

        Assert.Equal(1, r.Suggested);
        var cand = _h.Candidate(c);
        Assert.Equal(CandidateStatus.NeedsReview, cand.Status);
        Assert.Equal("embedding-cosine", cand.Suggestion);
        Assert.Equal(ca, _h.Sp(a).CanonicalProductId); // nothing linked
        Assert.Equal(cb, _h.Sp(b).CanonicalProductId);
        Assert.Equal(2, _h.Query(db => db.Products.Count()));
    }

    [Fact]
    public async Task TierB_HighCosineWithBrandOk_IsSuggested_AndABulkApplyThenMergesCanonicals_RecordsMethodAndKeepsPrices()
    {
        var (a, ca) = _h.AddListed(_h.ChainA, "Leite Meio Gordo");
        var (b, cb) = _h.AddListed(_h.ChainB, "Leite Gordo Meio");
        var c = _h.AddCandidate(a, b, 0.93);

        var r = await _h.RunMatchingAsync();

        Assert.Equal(1, r.Suggested);
        Assert.Equal(CandidateStatus.NeedsReview, _h.Candidate(c).Status);
        Assert.Equal("embedding-cosine", _h.Candidate(c).Suggestion);
        Assert.NotEqual(_h.Sp(a).CanonicalProductId, _h.Sp(b).CanonicalProductId);   // nothing linked yet

        Assert.Equal(1, (await BulkApplyAsync()).Applied);

        var spa = _h.Sp(a);
        var spb = _h.Sp(b);
        Assert.Equal(spa.CanonicalProductId, spb.CanonicalProductId);
        Assert.Contains("embedding-cosine", new[] { spa.MatchMethod, spb.MatchMethod });
        Assert.Equal(MatchStatus.AutoMatched, spb.MatchStatus);
        Assert.Equal(1, _h.Query(db => db.Products.Count()));   // the retired canonical is gone
        Assert.Equal(1, await _h.MultiChainAsync());
        var cand = _h.Candidate(c);
        Assert.Equal(CandidateStatus.Applied, cand.Status);
        Assert.Equal("embedding-cosine", cand.Method);
        Assert.NotNull(cand.DecidedAt);
        Assert.Equal("fake-embed", cand.ModelName);
    }

    [Fact]
    public async Task SizeUnknown_NeedsTheStricterThreshold_ElseGoesToTheJudge()
    {
        var (a, _) = _h.AddListed(_h.ChainA, "Arroz");
        var (b, _) = _h.AddListed(_h.ChainB, "Arroz");
        var (c1, _) = _h.AddListed(_h.ChainC, "Arroz");
        var mid = _h.AddCandidate(a, b, 0.93, sizeKnown: false);   // < 0.95
        var high = _h.AddCandidate(b, c1, 0.96, sizeKnown: false); // >= 0.95

        var r = await _h.RunMatchingAsync();

        Assert.Equal(1, r.Suggested);
        Assert.Equal(1, r.JudgeQueued);
        Assert.Equal(CandidateStatus.PendingJudge, _h.Candidate(mid).Status);
        Assert.Equal(CandidateStatus.NeedsReview, _h.Candidate(high).Status);
        Assert.Equal("embedding-cosine", _h.Candidate(high).Suggestion);
    }

    [Fact]
    public async Task UnknownBrand_IsNeverConfidentOnCosineAlone()
    {
        var (a, _) = _h.AddListed(_h.ChainA, "Arroz", brand: null);
        var (b, _) = _h.AddListed(_h.ChainB, "Arroz");
        var c = _h.AddCandidate(a, b, 0.99, brand: CandidateBrandCheck.Unknown);

        await _h.RunMatchingAsync();

        Assert.Equal(CandidateStatus.PendingJudge, _h.Candidate(c).Status);
    }

    [Fact]
    public async Task ReviewBandAndBelow()
    {
        var (a, _) = _h.AddListed(_h.ChainA, "P1");
        var (b, _) = _h.AddListed(_h.ChainB, "P2");
        var (c3, _) = _h.AddListed(_h.ChainC, "P3");
        var inReview = _h.AddCandidate(a, b, 0.75);
        var tooLow = _h.AddCandidate(b, c3, 0.65);

        var r = await _h.RunMatchingAsync();

        Assert.Equal(1, r.SentToReview);
        Assert.Equal(CandidateStatus.NeedsReview, _h.Candidate(inReview).Status);
        Assert.Equal(CandidateStatus.Proposed, _h.Candidate(tooLow).Status);
    }

    // --- Tier C: the judge ---------------------------------------------------------------------------------

    private (Guid Candidate, Guid A, Guid B) JudgeCase()
    {
        var (a, _) = _h.AddListed(_h.ChainA, "Iogurte Grego Natural");
        var (b, _) = _h.AddListed(_h.ChainB, "Iogurte Grego");
        return (_h.AddCandidate(a, b, 0.85), a, b);
    }

    [Fact]
    public async Task Judge_Yes_IsOnlyASuggestion_NothingIsMerged_AndBulkApplyNeverTakesIt()
    {
        var (c, a, b) = JudgeCase();
        _h.Judge.Verdict = JudgeVerdict.Yes;

        await _h.RunMatchingAsync();
        await DrainAsync();

        var cand = _h.Candidate(c);
        Assert.Equal(CandidateStatus.NeedsReview, cand.Status);
        Assert.Equal("embedding-judge", cand.Suggestion);
        Assert.Equal(JudgeVerdict.Yes, cand.JudgeVerdict);
        Assert.Equal("fake-judge", cand.JudgeModel);
        Assert.NotEqual(_h.Sp(a).CanonicalProductId, _h.Sp(b).CanonicalProductId);

        Assert.Equal(0, (await BulkApplyAsync(0.5)).Total);   // a judge answer is never bulk-applied
        Assert.NotEqual(_h.Sp(a).CanonicalProductId, _h.Sp(b).CanonicalProductId);
    }

    [Theory]
    [InlineData(JudgeVerdict.No)]
    [InlineData(JudgeVerdict.Unclear)]
    public async Task Judge_NoOrUnclear_GoesToTheReviewQueue_WithTheVerdict_AndNothingIsMerged(JudgeVerdict verdict)
    {
        var (c, a, b) = JudgeCase();
        _h.Judge.Verdict = verdict;

        await _h.RunMatchingAsync();
        await DrainAsync();

        var cand = _h.Candidate(c);
        Assert.Equal(CandidateStatus.NeedsReview, cand.Status);
        Assert.Equal(verdict, cand.JudgeVerdict);
        Assert.NotEqual(_h.Sp(a).CanonicalProductId, _h.Sp(b).CanonicalProductId);
    }

    [Fact]
    public async Task Judge_CallFails_IsNoDecision_StaysPending_AndIsRetriedNeverTreatedAsNo()
    {
        var (c, a, b) = JudgeCase();
        _h.Judge.Verdict = JudgeVerdict.Yes;
        await _h.RunMatchingAsync();
        _h.Faults.GoDown(FaultMode.Timeout);

        await DrainAsync();

        var pending = _h.Candidate(c);
        Assert.Equal(CandidateStatus.PendingJudge, pending.Status);
        Assert.Null(pending.JudgeVerdict);
        Assert.NotEqual(_h.Sp(a).CanonicalProductId, _h.Sp(b).CanonicalProductId);
        Assert.All(_h.Query(db => db.ModelJobs.ToList()), j => Assert.Equal(ModelJobStatus.Pending, j.Status));

        // Model back: the job is retried and now decides.
        _h.Faults.Recover();
        _h.Time.Advance(TimeSpan.FromMinutes(5));
        await DrainAsync(); // probe
        await DrainAsync();
        Assert.Equal(CandidateStatus.NeedsReview, _h.Candidate(c).Status);
        Assert.Equal(JudgeVerdict.Yes, _h.Candidate(c).JudgeVerdict);
    }

    [Fact]
    public async Task Judge_BadResponses_AreNoDecisionEither()
    {
        var (c, _, _) = JudgeCase();
        await _h.RunMatchingAsync();
        _h.Faults.GoDown(FaultMode.BadResponse);

        await DrainAsync();

        Assert.Equal(CandidateStatus.PendingJudge, _h.Candidate(c).Status);
        Assert.Null(_h.Candidate(c).JudgeVerdict);
    }

    [Fact]
    public async Task Matching_IsIdempotent_NoDuplicateJudgeJobsAndNoMerges()
    {
        var (_, _, _) = JudgeCase();
        var (a2, _) = _h.AddListed(_h.ChainA, "Leite");
        var (b2, _) = _h.AddListed(_h.ChainB, "Leite");
        _h.AddCandidate(a2, b2, 0.95);

        await _h.RunMatchingAsync();
        var second = await _h.RunMatchingAsync();

        Assert.Equal(1, _h.Query(db => db.ModelJobs.Count()));
        Assert.Equal(0, _h.Query(db => db.MatchMerges.Count()));   // a run never merges
        Assert.Equal(0, second.Suggested);
    }

    [Fact]
    public async Task CandidatesFromAnotherModelDigest_AreNotEvaluated()
    {
        _h.Services.GetRequiredService<CurrentModelState>().Info = new ModelInfo("fake-embed", "digest-2");
        var (a, _) = _h.AddListed(_h.ChainA, "Leite");
        var (b, _) = _h.AddListed(_h.ChainB, "Leite");
        var c = _h.AddCandidate(a, b, 0.97, digest: "digest-1");

        await _h.RunMatchingAsync();

        Assert.Equal(CandidateStatus.Proposed, _h.Candidate(c).Status);
    }

    // --- Safe merging ------------------------------------------------------------------------------------

    [Fact]
    public async Task SameChainOverlap_IsNotMergedByABulkApply_ButANoteExplainsWhy_AndHumanCanForceIt()
    {
        // canonical X = {a1 (chain A)}, canonical Y = {b1 (chain B), a2 (chain A)}: merging X and Y would put two chain-A prices on one product.
        var (a1, _) = _h.AddListed(_h.ChainA, "Leite 1L");
        var (b1, cy) = _h.AddListed(_h.ChainB, "Leite 1L");
        var a2 = _h.AddProduct(_h.ChainA, "Leite 6x1L", "Marca", 6, ProductUnit.L, cy);
        var c = _h.AddCandidate(a1, b1, 0.97);

        var batch = await RunThenBulkApplyAsync();

        Assert.Equal(1, batch.Blocked);
        var cand = _h.Candidate(c);
        Assert.Equal(CandidateStatus.NeedsReview, cand.Status);
        Assert.Contains("same chain", cand.Note);
        Assert.Equal(cy, _h.Sp(b1).CanonicalProductId);
        Assert.Equal(2, _h.Query(db => db.Products.Count()));

        var refused = await _h.HumanAcceptAsync(c);
        Assert.False(refused.Succeeded);
        var forced = await _h.HumanAcceptAsync(c, force: true);
        Assert.True(forced.Succeeded);
        Assert.Equal(_h.Sp(a1).CanonicalProductId, _h.Sp(b1).CanonicalProductId);
        Assert.Equal(_h.Sp(a2).CanonicalProductId, _h.Sp(b1).CanonicalProductId);
        Assert.Equal(MatchStatus.ManualMatched, _h.Sp(b1).MatchStatus);
    }

    [Fact]
    public async Task DifferentEans_BlockABulkMerge()
    {
        var (a, ca) = _h.AddListed(_h.ChainA, "Leite", ean: "5601234567890");
        var (b, cb) = _h.AddListed(_h.ChainB, "Leite", ean: "5609999999999");
        var c = _h.AddCandidate(a, b, 0.98);

        await RunThenBulkApplyAsync();

        var cand = _h.Candidate(c);
        Assert.Equal(CandidateStatus.NeedsReview, cand.Status);
        Assert.Contains("EAN", cand.Note);
        Assert.Equal(ca, _h.Sp(a).CanonicalProductId);
        Assert.Equal(cb, _h.Sp(b).CanonicalProductId);
    }

    [Fact]
    public async Task ManualDecisions_AreNeverOverwrittenByABulkApply_ButAHumanCanStillDecide()
    {
        var (a, ca) = _h.AddListed(_h.ChainA, "Leite");
        var (b, cb) = _h.AddListed(_h.ChainB, "Leite");
        _h.With(db => { var p = db.StoreProducts.Single(x => x.Id == b); p.MatchStatus = MatchStatus.ManualMatched; p.MatchMethod = "manual"; });
        var c = _h.AddCandidate(a, b, 0.99);

        await RunThenBulkApplyAsync();

        Assert.Equal(CandidateStatus.NeedsReview, _h.Candidate(c).Status);
        Assert.Equal(cb, _h.Sp(b).CanonicalProductId);
        Assert.Equal("manual", _h.Sp(b).MatchMethod);

        Assert.True((await _h.HumanAcceptAsync(c)).Succeeded);
        Assert.Equal(_h.Sp(a).CanonicalProductId, _h.Sp(b).CanonicalProductId);
    }

    [Fact]
    public async Task ManualProductInsideAGroup_ProtectsTheWholeGroupFromABulkMerge()
    {
        var (a, _) = _h.AddListed(_h.ChainA, "Leite");
        var (b, cb) = _h.AddListed(_h.ChainB, "Leite");
        var c3 = _h.AddProduct(_h.ChainC, "Leite", "Marca", 1, ProductUnit.L, cb);
        _h.With(db => db.StoreProducts.Single(x => x.Id == c3).MatchStatus = MatchStatus.ManualMatched);
        var c = _h.AddCandidate(a, b, 0.99);

        await RunThenBulkApplyAsync();

        Assert.Equal(CandidateStatus.NeedsReview, _h.Candidate(c).Status);
        Assert.Equal(cb, _h.Sp(c3).CanonicalProductId);
    }

    [Fact]
    public async Task Transitive_GroupsOfThreeMergeIntoOneCanonical()
    {
        var (a, _) = _h.AddListed(_h.ChainA, "Leite");
        var (b, _) = _h.AddListed(_h.ChainB, "Leite");
        var (c3, _) = _h.AddListed(_h.ChainC, "Leite");
        _h.AddCandidate(a, b, 0.97);
        _h.AddCandidate(b, c3, 0.96);
        _h.AddCandidate(a, c3, 0.95);

        var batch = await RunThenBulkApplyAsync();

        Assert.Equal(1, _h.Query(db => db.Products.Count()));
        Assert.Equal(1, _h.Query(db => db.StoreProducts.Select(s => s.CanonicalProductId).Distinct().Count()));
        Assert.Equal(3, batch.Applied); // the third pair is already together and is recorded as applied
        Assert.Equal(1, await _h.MultiChainAsync());
    }

    [Fact]
    public async Task Merge_RedirectsShoppingListItems_AndUndoRestoresEverything()
    {
        var (a, ca) = _h.AddListed(_h.ChainA, "Leite Meio Gordo");
        var (b, cb) = _h.AddListed(_h.ChainB, "Leite Gordo Meio");
        var listId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var category = Guid.NewGuid();
        _h.With(db =>
        {
            db.ProductCategories.Add(new ProductCategory { Id = category, Name = "Leite", Slug = "leite" });
            db.ShoppingLists.Add(new ShoppingList { Id = listId, Name = "Semana" });
            db.Products.Single(p => p.Id == cb).CategoryId = category;
        });
        _h.With(db => db.ShoppingListItems.Add(new ShoppingListItem { Id = itemId, ShoppingListId = listId, ProductId = cb, Quantity = 2 }));
        var c = _h.AddCandidate(a, b, 0.97);
        var before = (_h.Sp(a), _h.Sp(b));

        await RunThenBulkApplyAsync();

        // Whichever canonical survived, the list item follows the surviving product and the category is kept.
        var survivor = _h.Sp(a).CanonicalProductId!.Value;
        Assert.Equal(survivor, _h.Query(db => db.ShoppingListItems.Single(i => i.Id == itemId).ProductId));
        Assert.Equal(category, _h.Query(db => db.Products.Single(p => p.Id == survivor).CategoryId));

        var undo = await _h.UndoAsync(c);

        Assert.True(undo.Succeeded);
        Assert.Equal(before.Item1.CanonicalProductId, _h.Sp(a).CanonicalProductId);
        Assert.Equal(before.Item2.CanonicalProductId, _h.Sp(b).CanonicalProductId);
        Assert.Equal("created-new", _h.Sp(b).MatchMethod);
        Assert.Equal(cb, _h.Query(db => db.ShoppingListItems.Single(i => i.Id == itemId).ProductId));
        Assert.Equal(2, _h.Query(db => db.Products.Count(p => p.Id == ca || p.Id == cb)));
        Assert.Equal(category, _h.Query(db => db.Products.Single(p => p.Id == cb).CategoryId));
        var cand = _h.Candidate(c);
        Assert.Equal(CandidateStatus.Rejected, cand.Status); // an undone match is not proposed again
        Assert.NotNull(_h.Query(db => db.MatchMerges.Single().UndoneAt));
    }

    [Fact]
    public async Task Undo_IsRefused_WhenTheMergedProductWasMergedAgainLater()
    {
        var (a, _) = _h.AddListed(_h.ChainA, "Leite");
        var (b, _) = _h.AddListed(_h.ChainB, "Leite");
        var (c3, _) = _h.AddListed(_h.ChainC, "Leite");
        var first = _h.AddCandidate(a, b, 0.97);
        _h.AddCandidate(b, c3, 0.96);
        await RunThenBulkApplyAsync();
        // Simulate the survivor of `first` having been retired by a later merge.
        _h.With(db => db.MatchMerges.Single(m => m.CandidateId == first).SurvivorProductId = Guid.NewGuid());

        var undo = await _h.UndoAsync(first);

        Assert.False(undo.Succeeded);
    }

    [Fact]
    public async Task RejectedPairs_AreNeverProposedAgain_EvenAfterRegeneration()
    {
        var (a, _) = _h.AddListed(_h.ChainA, "Leite");
        var (b, _) = _h.AddListed(_h.ChainB, "Leite");
        _h.SetEmbedding(a, [1, 0, 0, 0]);
        _h.SetEmbedding(b, [1, 0, 0, 0]);
        await _h.GenerateAsync();
        var c = _h.Query(db => db.MatchCandidates.Single()).Id;
        _h.With(db => { var x = db.MatchCandidates.Single(); x.Status = CandidateStatus.Rejected; x.Method = "manual-review"; });

        await _h.GenerateAsync();
        var r = await _h.RunMatchingAsync();

        var cand = _h.Candidate(c);
        Assert.Equal(CandidateStatus.Rejected, cand.Status);
        Assert.Equal(0, r.Evaluated);
        Assert.NotEqual(_h.Sp(a).CanonicalProductId, _h.Sp(b).CanonicalProductId);
        Assert.Equal(1, _h.Query(db => db.MatchCandidates.Count()));
    }

    [Fact]
    public async Task Regeneration_KeepsAppliedAndDifferentVariantRows_EvenWhenThePairNoLongerQualifies()
    {
        var (a, _) = _h.AddListed(_h.ChainA, "Leite");
        var (b, _) = _h.AddListed(_h.ChainB, "Leite");
        var (c3, _) = _h.AddListed(_h.ChainC, "Leite");
        _h.SetEmbedding(a, [1, 0, 0, 0]);
        _h.SetEmbedding(b, [1, 0, 0, 0]);
        _h.SetEmbedding(c3, [0, 1, 0, 0]);
        var applied = _h.AddCandidate(a, c3, 0.5);   // a and c3 are orthogonal now: no longer generated
        var variant = _h.AddCandidate(b, c3, 0.5);
        _h.With(db =>
        {
            db.MatchCandidates.Single(x => x.Id == applied).Status = CandidateStatus.Applied;
            db.MatchCandidates.Single(x => x.Id == variant).Status = CandidateStatus.DifferentVariant;
        });

        await _h.GenerateAsync();

        Assert.Equal(CandidateStatus.Applied, _h.Candidate(applied).Status);
        Assert.Equal(CandidateStatus.DifferentVariant, _h.Candidate(variant).Status);
    }
}
