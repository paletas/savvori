using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Savvori.Shared;
using Savvori.WebApi;
using Savvori.WebApi.Modeling;

namespace Savvori.Web.Tests.Modeling;

public sealed class BulkMatchTests : IDisposable
{
    private readonly PipelineHost _h = new();
    public void Dispose() => _h.Dispose();

    private async Task<T> Scoped<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = _h.Services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    /// <summary>A dry-run style suggestion waiting in the review queue.</summary>
    private Guid Suggested(Guid chainA, Guid chainB, double cosine, string suggestion = "embedding-cosine",
        CandidateBrandCheck brand = CandidateBrandCheck.Ok, string? note = null)
    {
        var (a, _) = _h.AddListed(chainA, "Leite Meio Gordo");
        var (b, _) = _h.AddListed(chainB, "Leite M. Gordo");
        var id = _h.AddCandidate(a, b, cosine, brand: brand);
        _h.With(db =>
        {
            var c = db.MatchCandidates.Single(x => x.Id == id);
            c.Status = CandidateStatus.NeedsReview;
            c.Suggestion = suggestion;
            c.Note = note;
        });
        return id;
    }

    private Task<BulkBatch> RunApplyAsync(double min) => Scoped(async sp =>
    {
        var svc = sp.GetRequiredService<MatchBulkService>();
        var batch = await svc.CreateAsync(min, TestContext.Current.CancellationToken);
        await svc.RunApplyAsync(batch.Id, TestContext.Current.CancellationToken);
        return await sp.GetRequiredService<SavvoriDbContext>().BulkBatches.AsNoTracking().SingleAsync(b => b.Id == batch.Id);
    });

    [Fact]
    public async Task Eligible_IsOnlyConfidentCosineSuggestions_JudgeAnswersAndBlockedOnesAreExcluded()
    {
        var ok = Suggested(_h.ChainA, _h.ChainB, 0.95);
        Suggested(_h.ChainA, _h.ChainB, 0.85);                                        // below the threshold
        Suggested(_h.ChainA, _h.ChainB, 0.97, suggestion: "embedding-judge");         // a judge yes is never bulk-applied
        Suggested(_h.ChainA, _h.ChainB, 0.97, note: "blocked by a safety rule");
        Suggested(_h.ChainA, _h.ChainB, 0.97, brand: CandidateBrandCheck.Unknown);

        var eligible = await Scoped(async sp =>
            await sp.GetRequiredService<MatchBulkService>().Eligible(0.90).Select(c => c.Id).ToListAsync(TestContext.Current.CancellationToken));

        Assert.Equal([ok], eligible);
    }

    [Fact]
    public async Task Apply_MergesEveryEligiblePair_AsOneRun_AndLeavesBlockedOnesInTheQueueWithAReason()
    {
        var one = Suggested(_h.ChainA, _h.ChainB, 0.97);
        var two = Suggested(_h.ChainA, _h.ChainC, 0.93);
        var low = Suggested(_h.ChainA, _h.ChainB, 0.85);
        // a pair the safety rules refuse: canonical Y already has a chain-A price
        var (a1, _) = _h.AddListed(_h.ChainA, "Iogurte 1");
        var (b1, cy) = _h.AddListed(_h.ChainB, "Iogurte 1");
        _h.AddProduct(_h.ChainA, "Iogurte 6x", "Marca", 6, ProductUnit.L, cy);
        var blocked = _h.AddCandidate(a1, b1, 0.98);
        _h.With(db => { var c = db.MatchCandidates.Single(x => x.Id == blocked); c.Status = CandidateStatus.NeedsReview; c.Suggestion = "embedding-cosine"; });

        var batch = await RunApplyAsync(0.90);

        Assert.Equal(BulkBatchStatus.Done, batch.Status);
        Assert.Equal(3, batch.Total);
        Assert.Equal(2, batch.Applied);
        Assert.Equal(1, batch.Blocked);
        Assert.Equal(CandidateStatus.Applied, _h.Candidate(one).Status);
        Assert.Equal(CandidateStatus.Applied, _h.Candidate(two).Status);
        Assert.Equal("embedding-cosine", _h.Candidate(one).Method);
        Assert.Equal(CandidateStatus.NeedsReview, _h.Candidate(low).Status);            // untouched
        var stuck = _h.Candidate(blocked);
        Assert.Equal(CandidateStatus.NeedsReview, stuck.Status);
        Assert.Contains("same chain", stuck.Note);
        Assert.Equal(2, _h.Query(db => db.MatchMerges.Count(m => m.BatchId == batch.Id)));
    }

    [Fact]
    public async Task Undo_RestoresTheWholeRun_AndPutsThePairsBackInTheQueueNotInRejected()
    {
        var (a, ca) = _h.AddListed(_h.ChainA, "Leite Meio Gordo");
        var (b, cb) = _h.AddListed(_h.ChainB, "Leite M. Gordo");
        var c = _h.AddCandidate(a, b, 0.97);
        _h.With(db => { var x = db.MatchCandidates.Single(y => y.Id == c); x.Status = CandidateStatus.NeedsReview; x.Suggestion = "embedding-cosine"; });
        var listId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        _h.With(db => db.ShoppingLists.Add(new ShoppingList { Id = listId, Name = "Semana" }));
        _h.With(db => db.ShoppingListItems.Add(new ShoppingListItem { Id = itemId, ShoppingListId = listId, ProductId = cb, Quantity = 1 }));
        var batch = await RunApplyAsync(0.90);
        Assert.Equal(_h.Sp(a).CanonicalProductId, _h.Sp(b).CanonicalProductId);

        await Scoped(async sp =>
        {
            await sp.GetRequiredService<MatchBulkService>().RunUndoAsync(batch.Id, TestContext.Current.CancellationToken);
            return 0;
        });

        Assert.Equal(ca, _h.Sp(a).CanonicalProductId);
        Assert.Equal(cb, _h.Sp(b).CanonicalProductId);
        Assert.Equal(cb, _h.Query(db => db.ShoppingListItems.Single(i => i.Id == itemId).ProductId));
        var cand = _h.Candidate(c);
        Assert.Equal(CandidateStatus.NeedsReview, cand.Status);      // back in the queue, still suggested: nothing rejected
        Assert.Equal("embedding-cosine", cand.Suggestion);
        var stored = _h.Query(db => db.BulkBatches.AsNoTracking().Single(x => x.Id == batch.Id));
        Assert.Equal(BulkBatchStatus.Undone, stored.Status);
        Assert.Equal(1, stored.Undone);
    }

    [Fact]
    public async Task Runner_AllowsOneRunAtATime_AndWhenIdleCompletesAfterTheWork()
    {
        var runner = _h.Services.GetRequiredService<BulkRunner>();
        var gate = new TaskCompletionSource();

        Assert.True(runner.TryStart(Guid.NewGuid(), async _ => await gate.Task));
        Assert.True(runner.IsBusy);
        Assert.False(runner.TryStart(Guid.NewGuid(), _ => Task.CompletedTask)); // refused while busy

        gate.SetResult();
        await runner.WhenIdle;
        Assert.False(runner.IsBusy);
        Assert.True(runner.TryStart(Guid.NewGuid(), _ => Task.CompletedTask));
        await runner.WhenIdle;
    }

    [Fact]
    public async Task Runner_RecordsAFailure_OnTheBatch_AndFreesTheRunner()
    {
        var batch = new BulkBatch { Id = Guid.NewGuid(), Kind = "matches", Method = "x", Status = BulkBatchStatus.Running, CreatedAt = DateTime.UtcNow };
        _h.With(db => db.BulkBatches.Add(batch));
        var runner = _h.Services.GetRequiredService<BulkRunner>();

        runner.TryStart(batch.Id, _ => throw new InvalidOperationException("boom"));
        await runner.WhenIdle;

        var stored = _h.Query(db => db.BulkBatches.AsNoTracking().Single(b => b.Id == batch.Id));
        Assert.Equal(BulkBatchStatus.Failed, stored.Status);
        Assert.Equal("boom", stored.Error);
        Assert.False(runner.IsBusy);
    }

    [Fact]
    public async Task ARealRunWithTheDefaults_NeverAppliesAJudgeYes_ByItself()
    {
        Assert.False(new ModelOptions().Matching.AutoApplyJudgeYes);
        _h.Options.Matching.DryRun = false;
        var (a, _) = _h.AddListed(_h.ChainA, "Iogurte Grego Natural");
        var (b, _) = _h.AddListed(_h.ChainB, "Iogurte Grego");
        var c = _h.AddCandidate(a, b, 0.85);
        _h.Judge.Verdict = JudgeVerdict.Yes;
        await _h.RunMatchingAsync();
        await _h.DrainAsync();

        var cand = _h.Candidate(c);

        Assert.Equal(CandidateStatus.NeedsReview, cand.Status);
        Assert.Equal("embedding-judge", cand.Suggestion);
        Assert.NotEqual(_h.Sp(a).CanonicalProductId, _h.Sp(b).CanonicalProductId);
    }
}

public sealed class BulkCategoryTests : IDisposable
{
    private readonly PipelineHost _h = new();
    private readonly Guid _cat = Guid.NewGuid();
    public void Dispose() => _h.Dispose();

    public BulkCategoryTests() =>
        _h.With(db => db.ProductCategories.Add(new ProductCategory { Id = _cat, Name = "Leite", Slug = "leite" }));

    private Guid Suggestion(double confidence, string method = "embedding-knn", Guid? alreadyCategorised = null)
    {
        var product = Guid.NewGuid();
        var s = Guid.NewGuid();
        _h.With(db =>
        {
            db.Products.Add(new Product { Id = product, Name = "Leite", CategoryId = alreadyCategorised });
            db.CategorySuggestions.Add(new CategorySuggestion
            {
                Id = s, ProductId = product, SuggestedCategoryId = _cat, Confidence = confidence, Status = CategorySuggestionStatus.Suggested,
                Method = method, ModelName = "m", ModelDigest = "d", CreatedAt = DateTime.UtcNow
            });
        });
        return s;
    }

    private Product ProductOf(Guid suggestion) => _h.Query(db => db.CategorySuggestions.Where(x => x.Id == suggestion)
        .Select(x => x.Product).AsNoTracking().Single());

    private async Task<BulkBatch> ApplyAsync(double min)
    {
        using var scope = _h.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<CategoryBulkService>();
        var batch = await svc.CreateAsync(min, TestContext.Current.CancellationToken);
        await svc.RunApplyAsync(batch.Id, TestContext.Current.CancellationToken);
        return scope.ServiceProvider.GetRequiredService<SavvoriDbContext>().BulkBatches.AsNoTracking().Single(b => b.Id == batch.Id);
    }

    [Fact]
    public async Task Apply_AssignsOnlyPredictionsAboveTheThreshold_AndNeverOverwritesACategory()
    {
        var high = Suggestion(0.95);
        var low = Suggestion(0.70);
        var wholeString = Suggestion(0.99, method: "string-cache");
        var categorisedMeanwhile = Suggestion(0.96, alreadyCategorised: Guid.NewGuid());

        var batch = await ApplyAsync(0.90);

        Assert.Equal(BulkBatchStatus.Done, batch.Status);
        Assert.Equal(2, batch.Total);
        Assert.Equal(1, batch.Applied);
        Assert.Equal(1, batch.Blocked);
        Assert.Equal(_cat, ProductOf(high).CategoryId);
        Assert.Null(ProductOf(low).CategoryId);
        Assert.Null(ProductOf(wholeString).CategoryId);
        Assert.NotEqual(_cat, ProductOf(categorisedMeanwhile).CategoryId);
        var applied = _h.Query(db => db.CategorySuggestions.AsNoTracking().Single(s => s.Id == high));
        Assert.Equal(CategorySuggestionStatus.Applied, applied.Status);
        Assert.Equal("embedding-knn", applied.Method);          // still the model's decision, so it never trains the model
        Assert.Equal(batch.Id, applied.BatchId);
    }

    [Fact]
    public async Task Undo_RemovesTheRunsCategories_AndRequeuesTheSuggestions()
    {
        var a = Suggestion(0.95);
        var b = Suggestion(0.93);
        var batch = await ApplyAsync(0.90);
        // one product was re-categorised by hand after the run: the undo must leave that alone
        _h.With(db => db.Products.Single(p => p.Id == ProductOf(b).Id).CategoryId = Guid.NewGuid());

        using (var scope = _h.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<CategoryBulkService>().RunUndoAsync(batch.Id, TestContext.Current.CancellationToken);

        Assert.Null(ProductOf(a).CategoryId);
        Assert.NotNull(ProductOf(b).CategoryId);
        var back = _h.Query(db => db.CategorySuggestions.AsNoTracking().Single(s => s.Id == a));
        Assert.Equal(CategorySuggestionStatus.Suggested, back.Status);
        Assert.Null(back.BatchId);
        Assert.Equal(BulkBatchStatus.Undone, _h.Query(db => db.BulkBatches.AsNoTracking().Single(x => x.Id == batch.Id)).Status);
    }
}
