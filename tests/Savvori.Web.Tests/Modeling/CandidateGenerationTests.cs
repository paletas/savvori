using Microsoft.Extensions.DependencyInjection;
using Savvori.Shared;
using Savvori.WebApi;
using Savvori.WebApi.Modeling;

namespace Savvori.Web.Tests.Modeling;

public sealed class CandidateGenerationTests : IDisposable
{
    private readonly PipelineHost _h = new();
    public void Dispose() => _h.Dispose();

    // Unit-ish vectors in 4-D so cosine between two of them is easy to reason about.
    private static float[] V(double angleDegrees)
    {
        var r = angleDegrees * Math.PI / 180;
        return [(float)Math.Cos(r), (float)Math.Sin(r), 0f, 0f];
    }

    private List<MatchCandidate> Candidates() => _h.Query(db => db.MatchCandidates.ToList());

    [Fact]
    public async Task SimilarProductsFromDifferentChains_BecomeOneOrderedCandidate_WithScoreAndFlags()
    {
        var a = _h.AddProduct(_h.ChainA, "Leite Meio Gordo", "Mimosa", 1, ProductUnit.L);
        var b = _h.AddProduct(_h.ChainB, "Leite M. Gordo", "Mimosa", 1000, ProductUnit.Ml);
        _h.SetEmbedding(a, V(0));
        _h.SetEmbedding(b, V(10)); // cos(10deg) ~ 0.985

        var run = await _h.GenerateAsync();

        var c = Assert.Single(Candidates());
        Assert.True(c.StoreProductAId.CompareTo(c.StoreProductBId) < 0);
        Assert.Equal(new[] { a, b }.Order().ToArray(), new[] { c.StoreProductAId, c.StoreProductBId });
        Assert.InRange(c.Cosine, 0.98, 0.99);
        Assert.True(c.SizeKnown);
        Assert.Equal(CandidateBrandCheck.Ok, c.BrandCheck);
        Assert.Equal("fake-embed", c.ModelName);
        Assert.Equal("digest-1", c.ModelDigest);
        Assert.Equal(1, run.Added);
        Assert.Equal(1, run.Considered); // seen from both sides, counted once
    }

    [Fact]
    public async Task SameChainPairs_AndPairsBelowMinCosine_AreNeverCandidates()
    {
        var a1 = _h.AddProduct(_h.ChainA, "Leite", "Mimosa", 1, ProductUnit.L);
        var a2 = _h.AddProduct(_h.ChainA, "Leite 2", "Mimosa", 1, ProductUnit.L);
        var b = _h.AddProduct(_h.ChainB, "Sumo", "Mimosa", 1, ProductUnit.L);
        _h.SetEmbedding(a1, V(0));
        _h.SetEmbedding(a2, V(1));   // same chain as a1: excluded even though nearly identical
        _h.SetEmbedding(b, V(80));   // cos(80deg) ~ 0.17: below 0.6

        await _h.GenerateAsync();

        Assert.Empty(Candidates());
    }

    [Fact]
    public async Task HardFilters_RejectSizeConflicts_AndBrandConflicts_ButKeepUnknowns()
    {
        var a = _h.AddProduct(_h.ChainA, "Iogurte Natural", "Danone", 500, ProductUnit.G);
        var sizeConflict = _h.AddProduct(_h.ChainB, "Iogurte Natural", "Danone", 125, ProductUnit.G);
        var brandConflict = _h.AddProduct(_h.ChainB, "Iogurte Natural", "Auchan", 500, ProductUnit.G);
        var sizeUnknown = _h.AddProduct(_h.ChainC, "Iogurte Natural Danone", null, null, ProductUnit.Unit);
        foreach (var id in new[] { a, sizeConflict, brandConflict, sizeUnknown }) _h.SetEmbedding(id, V(0));

        var run = await _h.GenerateAsync();

        var pairs = Candidates().Select(c => (c.StoreProductAId, c.StoreProductBId)).ToHashSet();
        bool Has(Guid x, Guid y) => pairs.Contains(x.CompareTo(y) < 0 ? (x, y) : (y, x));
        Assert.False(Has(a, sizeConflict));   // 500 g vs 125 g
        Assert.False(Has(a, brandConflict));  // Danone vs Auchan
        Assert.True(Has(a, sizeUnknown));     // size unknown is kept, brand found in the name
        var kept = Candidates().Single(c => Has(a, sizeUnknown) && (c.StoreProductAId == a || c.StoreProductBId == a));
        Assert.False(kept.SizeKnown);                                  // flagged: needs stricter thresholds
        Assert.Equal(CandidateBrandCheck.Ok, kept.BrandCheck);         // brand found in the other listing's name
        Assert.True(run.RejectedSize >= 1);
        Assert.True(run.RejectedBrand >= 1);
    }

    [Fact]
    public async Task UnknownBrand_IsKeptAndFlaggedUnknown()
    {
        var a = _h.AddProduct(_h.ChainA, "Arroz Agulha", "Cigala", 1, ProductUnit.Kg);
        var b = _h.AddProduct(_h.ChainB, "Arroz Agulha", null, 1, ProductUnit.Kg);
        _h.SetEmbedding(a, V(0));
        _h.SetEmbedding(b, V(0));

        await _h.GenerateAsync();

        Assert.Equal(CandidateBrandCheck.Unknown, Assert.Single(Candidates()).BrandCheck);
    }

    [Fact]
    public async Task ProductsAlreadyOnTheSameCanonical_AreSkipped()
    {
        var canonical = Guid.NewGuid();
        _h.With(db => db.Products.Add(new Product { Id = canonical, Name = "Leite" }));
        var a = _h.AddProduct(_h.ChainA, "Leite", "Mimosa", 1, ProductUnit.L, canonical);
        var b = _h.AddProduct(_h.ChainB, "Leite", "Mimosa", 1, ProductUnit.L, canonical);
        _h.SetEmbedding(a, V(0));
        _h.SetEmbedding(b, V(0));

        var run = await _h.GenerateAsync();

        Assert.Empty(Candidates());
        Assert.Equal(1, run.AlreadySameCanonical);
    }

    [Fact]
    public async Task PartiallyEmbeddedCatalogue_YieldsFewerButCorrectCandidates()
    {
        var a = _h.AddProduct(_h.ChainA, "Leite", "Mimosa", 1, ProductUnit.L);
        var b = _h.AddProduct(_h.ChainB, "Leite", "Mimosa", 1, ProductUnit.L);
        var c = _h.AddProduct(_h.ChainC, "Leite", "Mimosa", 1, ProductUnit.L); // never embedded (model was down)
        _h.SetEmbedding(a, V(0));
        _h.SetEmbedding(b, V(5));

        await _h.GenerateAsync();
        var only = Assert.Single(Candidates());
        Assert.DoesNotContain(c, new[] { only.StoreProductAId, only.StoreProductBId });

        // Once c is embedded the next run adds its pairs, and keeps the existing one.
        _h.SetEmbedding(c, V(8), at: _h.Time.GetUtcNow().UtcDateTime.AddMinutes(1));
        await _h.GenerateAsync();
        Assert.Equal(3, Candidates().Count);
    }

    [Fact]
    public void TopNeighbours_ReturnsBestKFromOtherChainsOnly_AboveMinCosine_BestFirst()
    {
        var chainA = Guid.NewGuid();
        var chainB = Guid.NewGuid();
        IndexEntry E(Guid chain, double deg) => new(Guid.NewGuid(), chain, VectorCodec.Normalize(V(deg))!);
        var entries = new List<IndexEntry>
        {
            E(chainA, 0),                                            // 0: the probe
            E(chainA, 1),                                            // 1: same chain, must be ignored
            E(chainB, 20), E(chainB, 5), E(chainB, 10), E(chainB, 15), // 2..5, out of order on purpose
            E(chainB, 85)                                            // 6: below the minimum
        };
        var snap = new IndexSnapshot(entries, new ModelInfo("m", "d"), 4);

        var top = snap.TopNeighbors(0, k: 3, minCosine: 0.6);

        Assert.Equal(new[] { 3, 4, 5 }, top.Select(n => n.Row)); // 5deg, 10deg, 15deg
        Assert.True(top[0].Cosine > top[1].Cosine && top[1].Cosine > top[2].Cosine);
    }

    [Fact]
    public async Task VectorsFromAnotherModelIdentity_AreNeverComparedWithCurrentOnes()
    {
        var a = _h.AddProduct(_h.ChainA, "Leite", "Mimosa", 1, ProductUnit.L);
        var b = _h.AddProduct(_h.ChainB, "Leite", "Mimosa", 1, ProductUnit.L);
        _h.SetEmbedding(a, V(0), digest: "digest-1", at: _h.Time.GetUtcNow().UtcDateTime);
        _h.SetEmbedding(b, V(0), digest: "digest-2", at: _h.Time.GetUtcNow().UtcDateTime.AddMinutes(1)); // newer model wins

        var run = await _h.GenerateAsync();

        Assert.Empty(Candidates());      // a is stale (other digest) so excluded until recomputed
        Assert.Equal(1, run.Indexed);
    }

    [Fact]
    public async Task Regeneration_RemovesProposalsThatNoLongerQualify_AndUpdatesTheRest()
    {
        var a = _h.AddProduct(_h.ChainA, "Leite", "Mimosa", 1, ProductUnit.L);
        var b = _h.AddProduct(_h.ChainB, "Leite", "Mimosa", 1, ProductUnit.L);
        _h.SetEmbedding(a, V(0));
        _h.SetEmbedding(b, V(10));
        await _h.GenerateAsync();
        var firstId = Assert.Single(Candidates()).Id;

        // b changes so it no longer fits: different pack size.
        _h.With(db => db.StoreProducts.Single(p => p.Id == b).SizeValue = 2);
        var run = await _h.GenerateAsync();

        Assert.Empty(Candidates());
        Assert.Equal(1, run.Removed);

        _h.With(db => db.StoreProducts.Single(p => p.Id == b).SizeValue = 1);
        await _h.GenerateAsync();
        Assert.NotEqual(firstId, Assert.Single(Candidates()).Id);
    }

    [Fact]
    public async Task EmptyIndex_LeavesExistingCandidatesUntouched()
    {
        var a = _h.AddProduct(_h.ChainA, "Leite", "Mimosa", 1, ProductUnit.L);
        var b = _h.AddProduct(_h.ChainB, "Leite", "Mimosa", 1, ProductUnit.L);
        _h.SetEmbedding(a, V(0));
        _h.SetEmbedding(b, V(0));
        await _h.GenerateAsync();
        Assert.Single(Candidates());

        // All embeddings vanish (e.g. wiped for a model switch): proposals must not be silently deleted.
        _h.With(db => db.StoreProductEmbeddings.RemoveRange(db.StoreProductEmbeddings));
        var run = await _h.GenerateAsync();

        Assert.Single(Candidates());
        Assert.Equal(0, run.Indexed);
    }

    [Fact]
    public async Task Generation_NeverTouchesMatchesOrCanonicals()
    {
        var a = _h.AddProduct(_h.ChainA, "Leite", "Mimosa", 1, ProductUnit.L);
        var b = _h.AddProduct(_h.ChainB, "Leite", "Mimosa", 1, ProductUnit.L);
        _h.SetEmbedding(a, V(0));
        _h.SetEmbedding(b, V(0));

        await _h.GenerateAsync();

        Assert.All(_h.Query(db => db.StoreProducts.ToList()), p =>
        {
            Assert.Null(p.CanonicalProductId);
            Assert.Equal(MatchStatus.Unmatched, p.MatchStatus);
        });
    }
}

public sealed class EmbeddingIndexTests : IDisposable
{
    private readonly PipelineHost _h = new();
    public void Dispose() => _h.Dispose();

    private Task<IndexSnapshot> Refresh() =>
        _h.Services.GetRequiredService<EmbeddingIndex>().RefreshAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task Refresh_IsIncremental_PicksUpNewRowsAndDropsDeactivatedProducts()
    {
        var a = _h.AddProduct(_h.ChainA, "A");
        _h.SetEmbedding(a, [1, 0, 0, 0]);
        Assert.Equal(1, (await Refresh()).Count);

        var b = _h.AddProduct(_h.ChainB, "B");
        _h.SetEmbedding(b, [0, 1, 0, 0], at: _h.Time.GetUtcNow().UtcDateTime.AddMinutes(1));
        Assert.Equal(2, (await Refresh()).Count);

        _h.With(db => db.StoreProducts.Single(p => p.Id == a).IsActive = false);
        var snap = await Refresh();
        Assert.Equal(1, snap.Count);
        Assert.Equal(b, snap.Entries[0].StoreProductId);
    }

    [Fact]
    public async Task Vectors_AreNormalised_SoDotProductIsCosine()
    {
        var a = _h.AddProduct(_h.ChainA, "A");
        var b = _h.AddProduct(_h.ChainB, "B");
        _h.SetEmbedding(a, [3, 0, 0, 0]);
        _h.SetEmbedding(b, [5, 0, 0, 0]);

        var snap = await Refresh();

        Assert.Equal(1.0, IndexSnapshot.Dot(snap.Entries[0].Vector, snap.Entries[1].Vector), 5);
    }

    [Fact]
    public async Task OtherModelName_IsIgnoredEntirely()
    {
        var a = _h.AddProduct(_h.ChainA, "A");
        _h.SetEmbedding(a, [1, 0, 0, 0], model: "some-other-model");

        Assert.Equal(0, (await Refresh()).Count);
    }
}
