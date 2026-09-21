using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Quartz;
using Savvori.Shared;
using Savvori.WebApi;
using Savvori.WebApi.Modeling;

namespace Savvori.Web.Tests.Modeling;

/// <summary>Counts how many times the judge was actually asked.</summary>
public sealed class CountingJudge(IPairJudge inner) : IPairJudge
{
    public int Calls { get; private set; }
    public async Task<JudgeVerdict> JudgeAsync(JudgeItem a, JudgeItem b, CancellationToken ct = default)
    {
        Calls++;
        return await inner.JudgeAsync(a, b, ct);
    }
}

/// <summary>Real scanner, queue, drain job, handlers, index, candidate generator and matching over fakes.</summary>
public sealed class PipelineHost : IDisposable
{
    public ManualTimeProvider Time { get; } = new();
    public FaultPlan Faults { get; }
    public FakeEmbeddingClient Embedder { get; } = new() { Dimension = 16 };
    public FakePairJudge Judge { get; } = new();
    public CountingJudge JudgeCalls { get; }
    public ModelOptions Options { get; } = new()
    {
        Enabled = true, EmbeddingModel = "fake-embed", JudgeModel = "fake-judge", BatchSize = 4, MaxConcurrency = 1,
        Breaker = { FailureThreshold = 3, CooldownSeconds = 60 },
        Queue = { MaxAttempts = 3, BaseDelaySeconds = 10, MaxDelaySeconds = 100, LeaseSeconds = 120, MaxBatchesPerRun = 50 }
    };
    public ServiceProvider Services { get; }
    public ModelCircuitBreaker Breaker => Services.GetRequiredService<ModelCircuitBreaker>();
    public Guid ChainA { get; } = Guid.NewGuid();
    public Guid ChainB { get; } = Guid.NewGuid();
    public Guid ChainC { get; } = Guid.NewGuid();

    public PipelineHost()
    {
        Faults = new FaultPlan(Time);
        JudgeCalls = new CountingJudge(Judge);
        var dbName = $"Pipeline_{Guid.NewGuid()}";
        var s = new ServiceCollection();
        s.AddLogging();
        s.AddSingleton<TimeProvider>(Time);
        s.AddSingleton(Microsoft.Extensions.Options.Options.Create(Options));
        s.AddSingleton<ModelCircuitBreaker>();
        s.AddSingleton(Faults);
        s.AddSingleton<IEmbeddingClient>(sp => new BreakerEmbeddingClient(
            new FlakyEmbeddingClient(Embedder, sp.GetRequiredService<FaultPlan>()), sp.GetRequiredService<ModelCircuitBreaker>()));
        s.AddSingleton<IPairJudge>(sp => new BreakerPairJudge(
            new FlakyPairJudge(JudgeCalls, sp.GetRequiredService<FaultPlan>()), sp.GetRequiredService<ModelCircuitBreaker>()));
        s.AddDbContext<SavvoriDbContext>(o => o.UseInMemoryDatabase(dbName));
        s.AddScoped<ModelJobQueue>();
        s.AddSingleton<CurrentModelState>();
        s.AddSingleton<EmbeddingIndex>();
        s.AddScoped<EmbeddingScanner>();
        s.AddScoped<CandidateGenerator>();
        s.AddScoped<IModelJobHandler, EmbedJobHandler>();
        s.AddScoped<IModelJobHandler, JudgeJobHandler>();
        s.AddScoped<MatchApplier>();
        s.AddScoped<MatchingService>();
        s.AddScoped<CategoryClassifier>();
        s.AddTransient<ModelQueueDrainJob>();
        Services = s.BuildServiceProvider();

        With(db =>
        {
            foreach (var (id, slug) in new[] { (ChainA, "a"), (ChainB, "b"), (ChainC, "c") })
                db.StoreChains.Add(new StoreChain { Id = id, Name = slug, Slug = slug, BaseUrl = "https://x.test", IsActive = true });
        });
    }

    public void With(Action<SavvoriDbContext> action)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SavvoriDbContext>();
        action(db);
        db.SaveChanges();
    }

    public T Query<T>(Func<SavvoriDbContext, T> query)
    {
        using var scope = Services.CreateScope();
        return query(scope.ServiceProvider.GetRequiredService<SavvoriDbContext>());
    }

    public Guid AddProduct(Guid chain, string name, string? brand = null, decimal? size = null,
        ProductUnit unit = ProductUnit.Unit, Guid? canonical = null, bool active = true)
    {
        var id = Guid.NewGuid();
        With(db => db.StoreProducts.Add(new StoreProduct
        {
            Id = id, StoreChainId = chain, ExternalId = id.ToString("N"), Name = name, Brand = brand,
            SizeValue = size, Unit = unit, CanonicalProductId = canonical, IsActive = active,
            FirstSeen = Time.GetUtcNow().UtcDateTime, LastScraped = Time.GetUtcNow().UtcDateTime
        }));
        return id;
    }

    /// <summary>Stores a hand-made embedding (as the embed handler would) so similarity is under test control.</summary>
    public void SetEmbedding(Guid productId, float[] vector, string model = "fake-embed", string digest = "digest-1",
        string? hash = null, DateTime? at = null)
    {
        With(db =>
        {
            var p = db.StoreProducts.Single(x => x.Id == productId);
            var existing = db.StoreProductEmbeddings.SingleOrDefault(e => e.StoreProductId == productId);
            if (existing is not null) db.StoreProductEmbeddings.Remove(existing);
            db.StoreProductEmbeddings.Add(new StoreProductEmbedding
            {
                StoreProductId = productId, Vector = VectorCodec.ToBytes(vector), ModelName = model, ModelDigest = digest,
                Dimension = vector.Length, EmbeddedAt = at ?? Time.GetUtcNow().UtcDateTime,
                InputTextHash = hash ?? EmbeddingFreshness.HashText(EmbeddingFreshness.BuildInputText(p.Brand, p.Name))
            });
        });
    }

    public Guid AddCanonical(string name, string? ean = null, Guid? categoryId = null)
    {
        var id = Guid.NewGuid();
        With(db => db.Products.Add(new Product { Id = id, Name = name, EAN = ean, CategoryId = categoryId }));
        return id;
    }

    /// <summary>A store product with its own canonical, as the scraper creates them.</summary>
    public (Guid Sp, Guid Canonical) AddListed(Guid chain, string name, string? brand = "Marca", decimal? size = 1,
        ProductUnit unit = ProductUnit.L, string? ean = null)
    {
        var canonical = AddCanonical(name, ean);
        var sp = AddProduct(chain, name, brand, size, unit, canonical);
        With(db =>
        {
            var p = db.StoreProducts.Single(x => x.Id == sp);
            p.MatchStatus = MatchStatus.AutoMatched;
            p.MatchMethod = "created-new";
        });
        return (sp, canonical);
    }

    public Guid AddCandidate(Guid a, Guid b, double cosine, bool sizeKnown = true,
        CandidateBrandCheck brand = CandidateBrandCheck.Ok, string digest = "digest-1")
    {
        var id = Guid.NewGuid();
        var (x, y) = a.CompareTo(b) < 0 ? (a, b) : (b, a);
        With(db => db.MatchCandidates.Add(new MatchCandidate
        {
            Id = id, StoreProductAId = x, StoreProductBId = y, Cosine = cosine, SizeKnown = sizeKnown, BrandCheck = brand,
            ModelName = "fake-embed", ModelDigest = digest, CreatedAt = Time.GetUtcNow().UtcDateTime
        }));
        return id;
    }

    public async Task<int> MultiChainAsync()
    {
        using var scope = Services.CreateScope();
        return await WebApi.Controllers.MatchingAdminController.MultiChainCanonicalsAsync(
            scope.ServiceProvider.GetRequiredService<SavvoriDbContext>(), default);
    }

    public MatchCandidate Candidate(Guid id) => Query(db => db.MatchCandidates.AsNoTracking().Single(c => c.Id == id));
    public StoreProduct Sp(Guid id) => Query(db => db.StoreProducts.AsNoTracking().Single(c => c.Id == id));

    public async Task<MatchingRunResult> RunMatchingAsync()
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MatchingService>()
            .RunAsync(TestContext.Current.CancellationToken);
    }

    public async Task<ApplyResult> HumanAcceptAsync(Guid candidateId, bool force = false)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SavvoriDbContext>();
        var c = db.MatchCandidates.Single(x => x.Id == candidateId);
        return await scope.ServiceProvider.GetRequiredService<MatchApplier>()
            .ApplyAsync(c, MatchApplier.ManualMethod, manual: true, force, TestContext.Current.CancellationToken);
    }

    public async Task<ApplyResult> UndoAsync(Guid candidateId)
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MatchApplier>()
            .UndoAsync(candidateId, TestContext.Current.CancellationToken);
    }

    public async Task<ScanResult> ScanAsync(ModelInfo? current = null)
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<EmbeddingScanner>()
            .ScanAsync(current, TestContext.Current.CancellationToken);
    }

    public async Task<CandidateRunResult> GenerateAsync()
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CandidateGenerator>()
            .GenerateAsync(TestContext.Current.CancellationToken);
    }

    public Task DrainAsync()
    {
        var context = Substitute.For<IJobExecutionContext>();
        context.CancellationToken.Returns(TestContext.Current.CancellationToken);
        return Services.GetRequiredService<ModelQueueDrainJob>().Execute(context);
    }

    public void Dispose() => Services.Dispose();
}

public sealed class EmbeddingPipelineTests : IDisposable
{
    private readonly PipelineHost _h = new();
    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Scan_QueuesMissingProducts_AndDrainStoresEmbeddingsWithFullProvenance()
    {
        var ids = Enumerable.Range(0, 10).Select(i => _h.AddProduct(_h.ChainA, $"Produto {i}", "Marca")).ToList();

        var scan = await _h.ScanAsync();
        Assert.Equal(10, scan.Missing);
        Assert.Equal(10, scan.Enqueued);

        await _h.DrainAsync();

        Assert.Equal(3, _h.Embedder.Calls); // 10 jobs, batch size 4 -> 3 requests, not 10
        var stored = _h.Query(db => db.StoreProductEmbeddings.AsNoTracking().ToList());
        Assert.Equal(10, stored.Count);
        Assert.All(stored, e =>
        {
            Assert.Equal("fake-embed", e.ModelName);
            Assert.Equal("digest-1", e.ModelDigest);
            Assert.Equal(16, e.Dimension);
            Assert.Equal(16 * sizeof(float), e.Vector.Length);
            Assert.False(string.IsNullOrEmpty(e.InputTextHash));
        });
        Assert.All(_h.Query(db => db.ModelJobs.ToList()), j => Assert.Equal(ModelJobStatus.Done, j.Status));
        Assert.All(ids, id => Assert.Contains(stored, e => e.StoreProductId == id));
    }

    [Fact]
    public async Task Scan_IsIdempotent_WhileJobsArePending_AndNothingIsQueuedWhenFresh()
    {
        _h.AddProduct(_h.ChainA, "Leite", "Mimosa");

        Assert.Equal(1, (await _h.ScanAsync()).Enqueued);
        Assert.Equal(0, (await _h.ScanAsync()).Enqueued); // already pending
        await _h.DrainAsync();
        var third = await _h.ScanAsync();
        Assert.Equal(0, third.Missing + third.Stale);
        Assert.Equal(0, third.Enqueued);
    }

    [Fact]
    public async Task ChangedText_MarksEmbeddingStale_AndItIsRecomputed()
    {
        var id = _h.AddProduct(_h.ChainA, "Leite Meio Gordo", "Mimosa");
        await _h.ScanAsync();
        await _h.DrainAsync();
        var before = _h.Query(db => db.StoreProductEmbeddings.AsNoTracking().Single());

        _h.With(db => db.StoreProducts.Single(p => p.Id == id).Name = "Leite Magro");
        var scan = await _h.ScanAsync();
        Assert.Equal(1, scan.Stale);
        Assert.Equal(1, scan.Enqueued);

        _h.Time.Advance(TimeSpan.FromMinutes(1));
        await _h.DrainAsync();
        var after = _h.Query(db => db.StoreProductEmbeddings.AsNoTracking().Single());
        Assert.NotEqual(before.InputTextHash, after.InputTextHash);
        Assert.NotEqual(before.EmbeddedAt, after.EmbeddedAt);
        Assert.Equal(EmbeddingFreshness.HashText("mimosa leite magro"), after.InputTextHash);
    }

    [Fact]
    public async Task ChangedModelDigest_MarksEmbeddingsStale_AndRecomputesUnderTheNewIdentity()
    {
        _h.AddProduct(_h.ChainA, "Leite", "Mimosa");
        await _h.ScanAsync();
        await _h.DrainAsync();

        // The server now serves a different build of the same model.
        _h.Embedder.ModelDigest = "digest-2";
        var scan = await _h.ScanAsync(new ModelInfo("fake-embed", "digest-2"));
        Assert.Equal(1, scan.Stale);

        _h.Time.Advance(TimeSpan.FromMinutes(1));
        await _h.DrainAsync();
        Assert.Equal("digest-2", _h.Query(db => db.StoreProductEmbeddings.AsNoTracking().Single()).ModelDigest);
    }

    [Fact]
    public async Task UnknownCurrentDigest_DoesNotMarkEverythingStale()
    {
        _h.AddProduct(_h.ChainA, "Leite", "Mimosa");
        await _h.ScanAsync();
        await _h.DrainAsync();

        var scan = await _h.ScanAsync(current: null); // model unreachable during the scan
        Assert.Equal(0, scan.Stale);
    }

    [Fact]
    public async Task ObsoleteJob_TextChangedAfterQueueing_CompletesWithoutEmbeddingOldText()
    {
        var id = _h.AddProduct(_h.ChainA, "Leite", "Mimosa");
        await _h.ScanAsync();
        _h.With(db => db.StoreProducts.Single(p => p.Id == id).Name = "Leite Magro");

        await _h.DrainAsync();

        Assert.Equal(0, _h.Embedder.Calls);
        Assert.Empty(_h.Query(db => db.StoreProductEmbeddings.ToList()));
        Assert.All(_h.Query(db => db.ModelJobs.ToList()), j => Assert.Equal(ModelJobStatus.Done, j.Status));
        // ...and the next scan queues a job for the new text.
        Assert.Equal(1, (await _h.ScanAsync()).Enqueued);
    }

    [Fact]
    public async Task ModelDown_ProductsAreStillSaved_JobsStayPending_NothingIsEmbedded()
    {
        for (var i = 0; i < 6; i++) _h.AddProduct(_h.ChainA, $"P{i}");
        await _h.ScanAsync();
        _h.Faults.GoDown(FaultMode.Timeout);

        await _h.DrainAsync();

        Assert.Equal(6, _h.Query(db => db.StoreProducts.Count()));
        Assert.Empty(_h.Query(db => db.StoreProductEmbeddings.ToList()));
        Assert.All(_h.Query(db => db.ModelJobs.ToList()), j => Assert.Equal(ModelJobStatus.Pending, j.Status));

        // Recovery: everything gets embedded.
        _h.Faults.Recover();
        _h.Time.Advance(TimeSpan.FromMinutes(5));
        await _h.DrainAsync(); // probe closes the breaker
        await _h.DrainAsync();
        Assert.Equal(6, _h.Query(db => db.StoreProductEmbeddings.Count()));
    }

    [Fact]
    public async Task DeadLetteredJob_IsNotResurrectedByTheNextScan()
    {
        var id = _h.AddProduct(_h.ChainA, "Leite", "Mimosa");
        await _h.ScanAsync();
        _h.With(db => db.ModelJobs.Single().Status = ModelJobStatus.DeadLetter);

        Assert.Equal(0, (await _h.ScanAsync()).Enqueued);
        Assert.Equal(id, _h.Query(db => db.ModelJobs.Single().SubjectId));
    }

    [Fact]
    public async Task InactiveProducts_AreNotEmbedded()
    {
        _h.AddProduct(_h.ChainA, "Gone", "X", active: false);

        Assert.Equal(0, (await _h.ScanAsync()).Enqueued);
    }
}
