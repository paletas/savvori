using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Quartz;
using Savvori.Shared;
using Savvori.WebApi;
using Savvori.WebApi.Modeling;
using Savvori.WebApi.Scraping;

namespace Savvori.Web.Tests.Modeling;

/// <summary>Handler that embeds the job's subject text through the (breaker-guarded) client.</summary>
public sealed class RecordingEmbedHandler(IEmbeddingClient client) : IModelJobHandler
{
    public static readonly List<Guid> Processed = [];
    public ModelJobType Type => ModelJobType.Embed;

    public async Task HandleAsync(ModelJob job, CancellationToken ct)
    {
        await client.EmbedAsync([job.SubjectId.ToString()], ct);
        lock (Processed) Processed.Add(job.SubjectId);
    }
}

/// <summary>Wires the real queue, drain job, breaker and guarded client over fakes and an in-memory DB.</summary>
public sealed class ModelTestHost : IDisposable
{
    public ManualTimeProvider Time { get; } = new();
    public FaultPlan Faults { get; }
    public ModelOptions Options { get; } = new()
    {
        Enabled = true,
        EmbeddingModel = "fake",
        BatchSize = 10,
        MaxConcurrency = 1,
        Breaker = { FailureThreshold = 3, CooldownSeconds = 60 },
        Queue = { MaxAttempts = 3, BaseDelaySeconds = 10, MaxDelaySeconds = 100, LeaseSeconds = 120 }
    };
    public ServiceProvider Services { get; }

    public ModelTestHost()
    {
        Faults = new FaultPlan(Time);
        var dbName = $"ModelTests_{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(Options));
        services.AddSingleton<ModelCircuitBreaker>();
        services.AddSingleton<ModelTelemetry>();
        services.AddSingleton(Faults);
        services.AddSingleton<IEmbeddingClient>(sp => new BreakerEmbeddingClient(
            new FlakyEmbeddingClient(new FakeEmbeddingClient(), sp.GetRequiredService<FaultPlan>()),
            sp.GetRequiredService<ModelCircuitBreaker>()));
        services.AddDbContext<SavvoriDbContext>(o => o.UseInMemoryDatabase(dbName), ServiceLifetime.Scoped);
        services.AddScoped<ModelJobQueue>();
        services.AddScoped<IModelJobHandler>(sp => new RecordingEmbedHandler(sp.GetRequiredService<IEmbeddingClient>()));
        services.AddScoped<IModelStatusService, ModelStatusService>();
        services.AddSingleton<IStaleEmbeddingSource, NullStaleEmbeddingSource>();
        services.AddTransient<ModelQueueDrainJob>();
        Services = services.BuildServiceProvider();
        RecordingEmbedHandler.Processed.Clear();
    }

    public ModelCircuitBreaker Breaker => Services.GetRequiredService<ModelCircuitBreaker>();

    public async Task<T> WithDb<T>(Func<SavvoriDbContext, ModelJobQueue, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<SavvoriDbContext>(),
            scope.ServiceProvider.GetRequiredService<ModelJobQueue>());
    }

    public Task DrainAsync(CancellationToken ct)
    {
        var context = Substitute.For<IJobExecutionContext>();
        context.CancellationToken.Returns(ct);
        return Services.GetRequiredService<ModelQueueDrainJob>().Execute(context);
    }

    public void Dispose() => Services.Dispose();
}

public sealed class ModelQueueTests : IDisposable
{
    private readonly ModelTestHost _host = new();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    private Task<List<ModelJob>> Jobs() =>
        _host.WithDb((db, _) => db.ModelJobs.OrderBy(j => j.CreatedAt).ToListAsync(Ct));

    [Fact]
    public async Task Enqueue_IsIdempotent_ForSameSubjectAndInput_ButNotForChangedInput()
    {
        var subject = Guid.NewGuid();
        var a = await _host.WithDb((_, q) => q.EnqueueAsync(ModelJobType.Embed, subject, "h1", Ct));
        var again = await _host.WithDb((_, q) => q.EnqueueAsync(ModelJobType.Embed, subject, "h1", Ct));
        var changed = await _host.WithDb((_, q) => q.EnqueueAsync(ModelJobType.Embed, subject, "h2", Ct));

        Assert.Equal(a.Id, again.Id);
        Assert.NotEqual(a.Id, changed.Id);
        Assert.Equal(2, (await Jobs()).Count);
    }

    [Fact]
    public async Task Enqueue_AfterDone_CreatesNewJob()
    {
        var subject = Guid.NewGuid();
        var first = await _host.WithDb((_, q) => q.EnqueueAsync(ModelJobType.Embed, subject, "h", Ct));
        await _host.WithDb(async (_, q) => { await q.ClaimDueAsync([ModelJobType.Embed], 5, Ct); await q.CompleteAsync(first.Id, Ct); return 0; });

        var second = await _host.WithDb((_, q) => q.EnqueueAsync(ModelJobType.Embed, subject, "h", Ct));

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void Backoff_GrowsExponentially_IsCapped_AndJitterStaysInBounds()
    {
        var q = new ModelOptions.QueueOptions { BaseDelaySeconds = 10, MaxDelaySeconds = 100 };

        Assert.Equal(TimeSpan.FromSeconds(10), ModelJobQueue.ComputeBackoff(1, q, 1.0));
        Assert.Equal(TimeSpan.FromSeconds(20), ModelJobQueue.ComputeBackoff(2, q, 1.0));
        Assert.Equal(TimeSpan.FromSeconds(40), ModelJobQueue.ComputeBackoff(3, q, 1.0));
        Assert.Equal(TimeSpan.FromSeconds(100), ModelJobQueue.ComputeBackoff(20, q, 1.0)); // capped
        Assert.Equal(TimeSpan.FromSeconds(5), ModelJobQueue.ComputeBackoff(1, q, 0.0));    // jitter floor is 50%
    }

    [Fact]
    public async Task CountedFailure_BacksOff_ThenDeadLetters_AtMaxAttempts()
    {
        var job = await _host.WithDb((_, q) => q.EnqueueAsync(ModelJobType.Embed, Guid.NewGuid(), "h", Ct));

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            _host.Time.Advance(TimeSpan.FromMinutes(10)); // past any backoff
            var claimed = await _host.WithDb((_, q) => q.ClaimDueAsync([ModelJobType.Embed], 5, Ct));
            Assert.Equal([job.Id], claimed);
            await _host.WithDb(async (_, q) => { await q.FailAsync(job.Id, "bad", countAttempt: true, ct: Ct); return 0; });

            var state = (await Jobs()).Single();
            Assert.Equal(attempt, state.Attempts);
            Assert.Equal(attempt < 3 ? ModelJobStatus.Pending : ModelJobStatus.DeadLetter, state.Status);
            if (attempt < 3) Assert.True(state.NextAttemptAt > _host.Time.GetUtcNow().UtcDateTime);
        }

        // Dead letters are not claimed again...
        _host.Time.Advance(TimeSpan.FromDays(1));
        Assert.Empty(await _host.WithDb((_, q) => q.ClaimDueAsync([ModelJobType.Embed], 5, Ct)));

        // ...until an admin requeues them.
        Assert.Equal(1, await _host.WithDb((_, q) => q.RequeueDeadLettersAsync(Ct)));
        Assert.Single(await _host.WithDb((_, q) => q.ClaimDueAsync([ModelJobType.Embed], 5, Ct)));
    }

    [Fact]
    public async Task JobNotYetDue_IsNotClaimed()
    {
        var job = await _host.WithDb((_, q) => q.EnqueueAsync(ModelJobType.Embed, Guid.NewGuid(), "h", Ct));
        await _host.WithDb((_, q) => q.ClaimDueAsync([ModelJobType.Embed], 5, Ct));
        await _host.WithDb(async (_, q) => { await q.FailAsync(job.Id, "bad", true, ct: Ct); return 0; });

        Assert.Empty(await _host.WithDb((_, q) => q.ClaimDueAsync([ModelJobType.Embed], 5, Ct)));
    }

    [Fact]
    public async Task AbandonedRunningJob_IsReclaimedAfterLeaseExpires()
    {
        await _host.WithDb((_, q) => q.EnqueueAsync(ModelJobType.Embed, Guid.NewGuid(), "h", Ct));
        Assert.Single(await _host.WithDb((_, q) => q.ClaimDueAsync([ModelJobType.Embed], 5, Ct)));
        // worker "crashes": the job stays Running

        Assert.Empty(await _host.WithDb((_, q) => q.ClaimDueAsync([ModelJobType.Embed], 5, Ct)));
        _host.Time.Advance(TimeSpan.FromSeconds(121));
        Assert.Single(await _host.WithDb((_, q) => q.ClaimDueAsync([ModelJobType.Embed], 5, Ct)));
    }

    [Fact]
    public async Task Drain_WhenFlagOff_DoesNothing()
    {
        _host.Options.Enabled = false;
        await _host.WithDb((_, q) => q.EnqueueAsync(ModelJobType.Embed, Guid.NewGuid(), "h", Ct));

        await _host.DrainAsync(Ct);

        Assert.Equal(0, _host.Faults.Calls);
        Assert.Equal(ModelJobStatus.Pending, (await Jobs()).Single().Status);
    }

    [Fact]
    public async Task Drain_ProcessesJobs_WhenModelIsUp()
    {
        for (var i = 0; i < 4; i++)
            await _host.WithDb((_, q) => q.EnqueueAsync(ModelJobType.Embed, Guid.NewGuid(), "h", Ct));

        await _host.DrainAsync(Ct);

        Assert.All(await Jobs(), j => Assert.Equal(ModelJobStatus.Done, j.Status));
        Assert.Equal(4, RecordingEmbedHandler.Processed.Count);
    }

    [Fact]
    public async Task Drain_ModelDown_OpensBreaker_KeepsJobsPending_WithoutBurningAttempts_ThenRecovers()
    {
        for (var i = 0; i < 6; i++)
            await _host.WithDb((_, q) => q.EnqueueAsync(ModelJobType.Embed, Guid.NewGuid(), "h", Ct));
        _host.Faults.GoDown(FaultMode.ServerError);

        await _host.DrainAsync(Ct);

        Assert.Equal(BreakerState.Open, _host.Breaker.Snapshot().State);
        // 3 failures opened it; the rest were released without touching the backend.
        Assert.Equal(3, _host.Faults.Calls);
        var jobs = await Jobs();
        Assert.All(jobs, j => Assert.Equal(ModelJobStatus.Pending, j.Status));
        Assert.Empty(RecordingEmbedHandler.Processed);
        Assert.True(jobs.Count(j => j.Attempts == 0) >= 3, "released jobs must not lose attempts");

        // While the breaker is open, drain runs make no model calls beyond nothing at all.
        await _host.DrainAsync(Ct);
        Assert.Equal(3, _host.Faults.Calls);

        // Model comes back. After the cool-down the probe closes the breaker, and the next run drains everything.
        _host.Faults.Recover();
        _host.Time.Advance(TimeSpan.FromSeconds(61));
        await _host.DrainAsync(Ct);          // probe
        Assert.True(_host.Breaker.IsClosed);
        await _host.DrainAsync(Ct);          // work
        Assert.All(await Jobs(), j => Assert.Equal(ModelJobStatus.Done, j.Status));
        Assert.Equal(6, RecordingEmbedHandler.Processed.Count);
    }

    [Fact]
    public async Task Drain_LongOutage_NeverDeadLettersHealthyJobs()
    {
        await _host.WithDb((_, q) => q.EnqueueAsync(ModelJobType.Embed, Guid.NewGuid(), "h", Ct));
        _host.Faults.GoDown(FaultMode.Timeout);

        for (var i = 0; i < 30; i++)
        {
            await _host.DrainAsync(Ct);
            _host.Time.Advance(TimeSpan.FromMinutes(5));
        }

        Assert.Equal(ModelJobStatus.Pending, (await Jobs()).Single().Status);
    }

    [Fact]
    public async Task Drain_JobSpecificFailures_AreCounted_AndDeadLetter()
    {
        await _host.WithDb((_, q) => q.EnqueueAsync(ModelJobType.Embed, Guid.NewGuid(), "h", Ct));
        _host.Faults.GoDown(FaultMode.BadResponse); // server answers, but the answer is unusable

        for (var i = 0; i < 5; i++)
        {
            await _host.DrainAsync(Ct);
            _host.Time.Advance(TimeSpan.FromMinutes(10));
        }

        var job = (await Jobs()).Single();
        Assert.Equal(ModelJobStatus.DeadLetter, job.Status);
        Assert.Equal(3, job.Attempts);
        Assert.True(_host.Breaker.IsClosed);
    }

    [Fact]
    public async Task Status_ReportsBreakerQueueDepthOldestPendingAndDeadLetters()
    {
        ModelStatus idle;
        using (var idleScope = _host.Services.CreateScope())
            idle = await idleScope.ServiceProvider.GetRequiredService<IModelStatusService>().GetAsync(Ct);
        Assert.Equal(0, idle.QueueDepth);
        Assert.Null(idle.OldestPendingAt);
        Assert.Equal("Closed", idle.BreakerState);

        await _host.WithDb((_, q) => q.EnqueueAsync(ModelJobType.Embed, Guid.NewGuid(), "h", Ct));
        _host.Time.Advance(TimeSpan.FromMinutes(1));
        await _host.WithDb((_, q) => q.EnqueueAsync(ModelJobType.Embed, Guid.NewGuid(), "h", Ct));
        _host.Faults.GoDown(FaultMode.Timeout);
        await _host.DrainAsync(Ct);

        using var scope = _host.Services.CreateScope();
        var status = await scope.ServiceProvider.GetRequiredService<IModelStatusService>().GetAsync(Ct);

        Assert.Equal(2, status.QueueDepth);
        Assert.Equal(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), status.OldestPendingAt?.ToUniversalTime());
        Assert.NotNull(status.LastError);
        Assert.NotNull(status.LastErrorAt);
        Assert.Equal(0, status.StaleEmbeddings);
    }
}

/// <summary>
/// Rule 1: the model being down must never affect scraping. Runs the real scrape job while the
/// model is failing and jobs sit in the queue.
/// </summary>
public sealed class ScrapingWithModelDownTests : IDisposable
{
    private readonly ModelTestHost _host = new();
    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Scraping_SavesProducts_WhileModelIsDown_AndQueueIsBackedUp()
    {
        var ct = TestContext.Current.CancellationToken;
        _host.Faults.GoDown(FaultMode.Timeout);
        var chainId = Guid.NewGuid();

        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SavvoriDbContext>();
        db.StoreChains.Add(new StoreChain { Id = chainId, Name = "Pingo Doce", Slug = "pingodoce", BaseUrl = "https://x.test", IsActive = true });
        await db.SaveChangesAsync(ct);
        await CategorySeeder.SeedAsync(db);

        // Model work is pending and failing in the background...
        var queue = scope.ServiceProvider.GetRequiredService<ModelJobQueue>();
        await queue.EnqueueAsync(ModelJobType.Embed, Guid.NewGuid(), "h", ct);
        await _host.DrainAsync(ct);
        Assert.True(_host.Faults.Calls > 0); // the drain really did hit the down model

        // ...and the scrape still completes and stores the product exactly as before.
        using var telemetry = new ScrapingTelemetry();
        var job = new StoreScrapeJob(
            [new OneProductScraper()],
            new ScraperResultProcessor(db, NullLogger<ScraperResultProcessor>.Instance),
            db, telemetry, NullLogger<StoreScrapeJob>.Instance);
        var context = Substitute.For<IJobExecutionContext>();
        context.MergedJobDataMap.Returns(new JobDataMap
        {
            { StoreScrapeJob.StoreChainSlugKey, "pingodoce" },
            { StoreScrapeJob.ScrapeLocationsKey, false }
        });
        context.CancellationToken.Returns(ct);

        await job.Execute(context);

        var scrape = await db.ScrapingJobs.SingleAsync(ct);
        Assert.Equal(ScrapingJobStatus.Completed, scrape.Status);
        var stored = await db.StoreProducts.SingleAsync(ct);
        Assert.Equal("Leite Meio Gordo 1L", stored.Name);
        Assert.NotNull(stored.CanonicalProductId);
        Assert.Equal(MatchStatus.AutoMatched, stored.MatchStatus);
    }

    private sealed class OneProductScraper : IStoreScraper
    {
        public string StoreChainSlug => "pingodoce";

        public Task<IReadOnlyList<ScrapedProduct>> ScrapeProductsAsync(string? category = null, CancellationToken ct = default)
        {
            IReadOnlyList<ScrapedProduct> products =
            [
                new ScrapedProduct("Leite Meio Gordo 1L", "Marca Teste", "leite", 1.09m, 1.09m, null, "t-1",
                    null, "https://example.test/1", false, null, ProductUnit.L, 1m)
            ];
            return Task.FromResult(products);
        }

        public Task<IReadOnlyList<ScrapedStoreLocation>> ScrapeStoreLocationsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScrapedStoreLocation>>([]);
    }
}
