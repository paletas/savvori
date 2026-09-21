using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Savvori.Shared;
using Savvori.WebApi;
using Savvori.WebApi.Modeling;

namespace Savvori.Web.Tests.Modeling;

public sealed class KnnVotingTests
{
    private static readonly Guid X = Guid.NewGuid();
    private static readonly Guid Y = Guid.NewGuid();

    [Fact]
    public void Tally_IsSimilarityWeighted_AndConfidenceIsTheWinnersShare()
    {
        var vote = KnnVoting.Tally([(X, 0.9), (X, 0.8), (Y, 0.3)])!;

        Assert.Equal(X, vote.CategoryId);
        Assert.Equal(1.7 / 2.0, vote.Confidence, 6);
        Assert.Equal(Y, vote.RunnerUp);
        Assert.Equal(3, vote.Neighbours);
    }

    [Fact]
    public void Tally_ManyModerateNeighboursCanOutvoteOneVeryClose()
    {
        var vote = KnnVoting.Tally([(X, 0.95), (Y, 0.5), (Y, 0.5), (Y, 0.5)])!;

        Assert.Equal(Y, vote.CategoryId);
        Assert.Equal(1.5 / 2.45, vote.Confidence, 6);
    }

    [Fact]
    public void Tally_NoVotes_IsNull() => Assert.Null(KnnVoting.Tally([]));
}

public sealed class CategoryClassifierTests : IDisposable
{
    private readonly PipelineHost _h = new();
    private readonly Guid _catX = Guid.NewGuid();
    private readonly Guid _catY = Guid.NewGuid();

    public CategoryClassifierTests()
    {
        _h.Options.Categories.DryRun = false;
        _h.With(db =>
        {
            db.ProductCategories.Add(new ProductCategory { Id = _catX, Name = "Leite", Slug = "leite" });
            db.ProductCategories.Add(new ProductCategory { Id = _catY, Name = "Sumos", Slug = "sumos" });
        });
    }

    public void Dispose() => _h.Dispose();

    private static float[] V(double deg)
    {
        var r = deg * Math.PI / 180;
        return [(float)Math.Cos(r), (float)Math.Sin(r), 0f, 0f];
    }

    /// <summary>A categorised product with an embedding at the given angle.</summary>
    private Guid Labelled(Guid chain, Guid category, double deg, string name = "Produto")
    {
        var (sp, canonical) = _h.AddListed(chain, name);
        _h.With(db => db.Products.Single(p => p.Id == canonical).CategoryId = category);
        _h.SetEmbedding(sp, V(deg));
        return canonical;
    }

    /// <summary>An uncategorised product with an embedding and an optional raw scraped category string.</summary>
    private Guid Unlabelled(Guid chain, double deg, string? raw = null, string name = "Novo")
    {
        var (sp, canonical) = _h.AddListed(chain, name);
        _h.With(db => db.Products.Single(p => p.Id == canonical).Category = raw);
        _h.SetEmbedding(sp, V(deg));
        return canonical;
    }

    private void SeedClusters()
    {
        for (var i = 0; i < 6; i++) Labelled(i % 2 == 0 ? _h.ChainA : _h.ChainB, _catX, 0 + i, $"Leite {i}");
        for (var i = 0; i < 6; i++) Labelled(i % 2 == 0 ? _h.ChainA : _h.ChainB, _catY, 90 + i, $"Sumo {i}");
    }

    private Product Prod(Guid id) => _h.Query(db => db.Products.AsNoTracking().Single(p => p.Id == id));

    private CategorySuggestion? Sugg(Guid productId) =>
        _h.Query(db => db.CategorySuggestions.AsNoTracking().SingleOrDefault(s => s.ProductId == productId));

    private async Task<ClassifierRunResult> RunAsync()
    {
        using var scope = _h.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CategoryClassifier>().RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FeatureOff_OrBreakerOpen_DecidesNothing()
    {
        SeedClusters();
        var p = Unlabelled(_h.ChainC, 2);

        _h.Options.Enabled = false;
        Assert.NotNull((await RunAsync()).SkippedReason);
        _h.Options.Enabled = true;
        for (var i = 0; i < 3; i++) _h.Breaker.RecordFailure("down");
        var degraded = await RunAsync();

        Assert.Contains("rule-based", degraded.SkippedReason);
        Assert.Null(Prod(p).CategoryId);
        Assert.Null(Sugg(p));
    }

    [Fact]
    public async Task ConfidentPrediction_IsAssigned_WithMethodScoreModelAndTimestamp()
    {
        SeedClusters();
        var p = Unlabelled(_h.ChainC, 3);

        var r = await RunAsync();

        Assert.Equal(1, r.AutoAssigned);
        Assert.Equal(_catX, Prod(p).CategoryId);
        var s = Sugg(p)!;
        Assert.Equal(CategorySuggestionStatus.Applied, s.Status);
        Assert.Equal("embedding-knn", s.Method);
        Assert.InRange(s.Confidence, 0.85, 1.0);
        Assert.Equal("fake-embed", s.ModelName);
        Assert.Equal("digest-1", s.ModelDigest);
        Assert.NotNull(s.DecidedAt);
        Assert.Null(s.PreviousCategoryId);
    }

    [Fact]
    public async Task DryRun_IsTheDefault_PredictionsGoToTheReviewQueueAndNothingIsAssigned()
    {
        Assert.True(new ModelOptions().Categories.DryRun);
        _h.Options.Categories.DryRun = true;
        SeedClusters();
        var p = Unlabelled(_h.ChainC, 3);

        var r = await RunAsync();

        Assert.Equal(1, r.WouldAssign);
        Assert.Null(Prod(p).CategoryId);
        Assert.Equal(CategorySuggestionStatus.Suggested, Sugg(p)!.Status);
    }

    [Fact]
    public async Task ExistingCategories_AreNeverTouched()
    {
        SeedClusters();
        var wrongButLabelled = Labelled(_h.ChainC, _catY, 3, "Leite fresco mal etiquetado"); // looks like X, labelled Y

        await RunAsync();

        Assert.Equal(_catY, Prod(wrongButLabelled).CategoryId);
        Assert.Null(Sugg(wrongButLabelled));
    }

    [Fact]
    public async Task VeryUncertainOrFarAwayProducts_StayUncategorised()
    {
        SeedClusters();
        var lone = Unlabelled(_h.ChainC, 200); // no neighbour is similar enough to vote

        var r = await RunAsync();

        Assert.Null(Prod(lone).CategoryId);
        Assert.Null(Sugg(lone));
        Assert.True(r.NoSuggestion >= 1);
    }

    [Fact]
    public async Task MixedNeighbourhood_GoesToReviewNotAutoAssigned()
    {
        SeedClusters();
        // Between the clusters: half the neighbours vote X and half vote Y, so confidence sits far below 0.85.
        var borderline = Unlabelled(_h.ChainC, 47.5);
        _h.Options.Categories.MinNeighbourCosine = 0.0;

        await RunAsync();

        Assert.Null(Prod(borderline).CategoryId);
        var s = Sugg(borderline);
        Assert.True(s is null || s.Status == CategorySuggestionStatus.Suggested);
    }

    [Fact]
    public async Task Thresholds_AreConfigurable()
    {
        SeedClusters();
        var p = Unlabelled(_h.ChainC, 3);
        _h.Options.Categories.AutoAssignConfidence = 1.01; // impossible: everything must be reviewed

        var r = await RunAsync();

        Assert.Equal(0, r.AutoAssigned);
        Assert.Equal(CategorySuggestionStatus.Suggested, Sugg(p)!.Status);
    }

    [Fact]
    public async Task DoesNotLearnFromItsOwnDecisions()
    {
        for (var i = 0; i < 3; i++) Labelled(_h.ChainA, _catX, i); // only 3 real labels, all near angle 0
        var first = Unlabelled(_h.ChainB, 1);
        await RunAsync();
        Assert.Equal(_catX, Prod(first).CategoryId);

        // A product far from the real labels but right next to the model-labelled one must get no vote from it.
        var second = Unlabelled(_h.ChainC, 80);
        var firstListing = _h.Query(db => db.StoreProducts.Single(s => s.CanonicalProductId == first).Id);
        _h.SetEmbedding(firstListing, V(80));
        await RunAsync();

        Assert.Null(Prod(second).CategoryId);
    }

    [Fact]
    public async Task RejectedSuggestions_AreNeverProposedAgain()
    {
        _h.Options.Categories.DryRun = true;
        SeedClusters();
        var p = Unlabelled(_h.ChainC, 3);
        await RunAsync();
        using (var scope = _h.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<CategoryClassifier>()
                .RejectAsync(Sugg(p)!.Id, TestContext.Current.CancellationToken);

        _h.Options.Categories.DryRun = false;
        var r = await RunAsync();

        Assert.Equal(0, r.AutoAssigned);
        Assert.Null(Prod(p).CategoryId);
        Assert.Equal(CategorySuggestionStatus.Rejected, Sugg(p)!.Status);
    }

    [Fact]
    public async Task HumanAccept_SetsTheCategory_AndUndoRestoresIt()
    {
        _h.Options.Categories.DryRun = true;
        SeedClusters();
        var p = Unlabelled(_h.ChainC, 3);
        await RunAsync();
        var id = Sugg(p)!.Id;

        using (var scope = _h.Services.CreateScope())
        {
            var c = scope.ServiceProvider.GetRequiredService<CategoryClassifier>();
            Assert.True((await c.AcceptAsync(id, TestContext.Current.CancellationToken)).Ok);
            Assert.Equal(_catX, Prod(p).CategoryId);
            Assert.Equal("manual-review", Sugg(p)!.Method);
            Assert.True((await c.UndoAsync(id, TestContext.Current.CancellationToken)).Ok);
        }

        Assert.Null(Prod(p).CategoryId);
        Assert.Equal(CategorySuggestionStatus.Rejected, Sugg(p)!.Status);
    }

    [Fact]
    public async Task HumanAccept_IsRefused_IfTheProductWasCategorisedInTheMeantime()
    {
        _h.Options.Categories.DryRun = true;
        SeedClusters();
        var p = Unlabelled(_h.ChainC, 3);
        await RunAsync();
        _h.With(db => db.Products.Single(x => x.Id == p).CategoryId = _catY); // you categorised it by hand

        using var scope = _h.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<CategoryClassifier>()
            .AcceptAsync(Sugg(p)!.Id, TestContext.Current.CancellationToken);

        Assert.False(result.Ok);
        Assert.Equal(_catY, Prod(p).CategoryId);
    }

    // --- store-category strings, decided once ---------------------------------------------------------

    [Fact]
    public async Task HomogeneousStoreString_IsDecidedOnce_CachedAndReusedForNewProducts()
    {
        SeedClusters();
        var group = Enumerable.Range(0, 6)
            .Select(i => Unlabelled(_h.ChainC, 2 + i * 0.5, raw: "Produtos Lácteos", name: $"L{i}")).ToList();

        var r = await RunAsync();

        Assert.Equal(1, r.StringsDecided);
        var decision = _h.Query(db => db.CategoryStringDecisions.AsNoTracking().Single());
        Assert.Equal("produtos lacteos", decision.RawString);
        Assert.Equal(_catX, decision.CategoryId);
        Assert.Equal(CategorySuggestionStatus.Applied, decision.Status);
        Assert.Equal(6, decision.Support);
        Assert.All(group, g =>
        {
            Assert.Equal(_catX, Prod(g).CategoryId);
            Assert.Equal("string-cache", Sugg(g)!.Method);
        });

        // A new product with the same string reuses the cached decision, even though its own vector looks odd.
        var later = Unlabelled(_h.ChainC, 45, raw: "Produtos lácteos", name: "Estranho");
        await RunAsync();
        Assert.Equal(_catX, Prod(later).CategoryId);
        Assert.Equal("string-cache", Sugg(later)!.Method);
        Assert.Equal(1, _h.Query(db => db.CategoryStringDecisions.Count()));
    }

    [Fact]
    public async Task MixedStoreString_LikeAGenericAlimentacao_IsNotDecidedAsAWhole_ButProductsAreDecidedOneByOne()
    {
        SeedClusters();
        var milk = Enumerable.Range(0, 3).Select(i => Unlabelled(_h.ChainC, 2 + i, raw: "alimentacao", name: $"M{i}")).ToList();
        var juice = Enumerable.Range(0, 3).Select(i => Unlabelled(_h.ChainC, 92 + i, raw: "alimentacao", name: $"J{i}")).ToList();
        _h.Options.Categories.MinNeighbourCosine = 0.0;

        var r = await RunAsync();

        Assert.Equal(1, r.StringsMixed);
        Assert.Equal(CategorySuggestionStatus.Mixed, _h.Query(db => db.CategoryStringDecisions.Single()).Status);
        Assert.All(milk, m => Assert.Equal(_catX, Prod(m).CategoryId));
        Assert.All(juice, j => Assert.Equal(_catY, Prod(j).CategoryId));
    }

    [Fact]
    public async Task SmallStrings_AreNotDecidedAsAWhole()
    {
        SeedClusters();
        Unlabelled(_h.ChainC, 2, raw: "rara", name: "R1");
        Unlabelled(_h.ChainC, 3, raw: "rara", name: "R2");

        var r = await RunAsync();

        Assert.Equal(0, r.StringsDecided + r.StringsMixed);
        Assert.Empty(_h.Query(db => db.CategoryStringDecisions.ToList()));
    }

    [Fact]
    public async Task DryRun_StringDecision_IsASuggestion_AndAcceptingItCategorisesEveryProductWithTheString()
    {
        _h.Options.Categories.DryRun = true;
        SeedClusters();
        var group = Enumerable.Range(0, 6)
            .Select(i => Unlabelled(_h.ChainC, 2 + i * 0.5, raw: "laticinios", name: $"L{i}")).ToList();
        await RunAsync();
        var d = _h.Query(db => db.CategoryStringDecisions.AsNoTracking().Single());
        Assert.Equal(CategorySuggestionStatus.Suggested, d.Status);
        Assert.All(group, g => Assert.Null(Prod(g).CategoryId));

        using var scope = _h.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<CategoryClassifier>()
            .AcceptStringAsync(d.Id, TestContext.Current.CancellationToken);

        Assert.True(result.Ok);
        Assert.Equal(6, result.Updated);
        Assert.All(group, g => Assert.Equal(_catX, Prod(g).CategoryId));
    }
}
