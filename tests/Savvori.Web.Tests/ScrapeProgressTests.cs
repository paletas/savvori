using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;
using Savvori.Shared;
using Savvori.WebApi;
using Savvori.WebApi.Scraping;
using Savvori.WebApp.Services.ApiModels;

namespace Savvori.Web.Tests;

public class ProgressThrottleTests
{
    [Fact]
    public void FirstPasses_ThenGatedUntilIntervalElapsed()
    {
        long now = 0;
        var t = new ProgressThrottle(TimeSpan.FromSeconds(15), () => now);
        Assert.True(t.ShouldEmit());
        now = TimeSpan.FromSeconds(14).Ticks;
        Assert.False(t.ShouldEmit());
        now = TimeSpan.FromSeconds(15).Ticks;
        Assert.True(t.ShouldEmit());
        now += TimeSpan.FromSeconds(1).Ticks;
        Assert.False(t.ShouldEmit());
    }
}

public class ScrapeProgressEstimatorTests
{
    private static readonly DateTime Start = new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Threshold = TimeSpan.FromSeconds(60);

    private static ScrapingJob Running(Action<ScrapingJob>? tweak = null)
    {
        var j = new ScrapingJob
        {
            Id = Guid.NewGuid(), Status = ScrapingJobStatus.Running, StartedAt = Start,
            ProgressProductsSeen = 1000, LastProgressAt = Start.AddSeconds(100),
            LastHeartbeatAt = Start.AddSeconds(100)
        };
        tweak?.Invoke(j);
        return j;
    }

    [Fact]
    public void DeclaredTotal_EtaFromAverageRate_NoEstimateFlag()
    {
        var v = ScrapeProgressEstimator.Build(Running(j => j.ProgressTotal = 2000), 9999, Start.AddSeconds(100), Threshold)!;
        Assert.Equal(2000, v.Total);
        Assert.False(v.TotalIsEstimate);
        Assert.False(v.EtaIsEstimate);
        Assert.Equal(100, v.EtaSeconds!.Value, 3); // 1000 left at 10 products/s
    }

    [Fact]
    public void NoDeclaredTotal_UsesPreviousHealthyCountAsEstimate()
    {
        var v = ScrapeProgressEstimator.Build(Running(), 3000, Start.AddSeconds(100), Threshold)!;
        Assert.Equal(3000, v.Total);
        Assert.True(v.TotalIsEstimate);
        Assert.True(v.EtaIsEstimate);
        Assert.Equal(200, v.EtaSeconds!.Value, 3);
    }

    [Fact]
    public void NoTotalAtAll_EtaNull()
    {
        var v = ScrapeProgressEstimator.Build(Running(), null, Start.AddSeconds(100), Threshold)!;
        Assert.Null(v.Total);
        Assert.True(v.TotalIsEstimate);
        Assert.Null(v.EtaSeconds);
    }

    [Fact]
    public void NoTotal_FallsBackToKeysDoneOverTotal_AsEstimate()
    {
        var v = ScrapeProgressEstimator.Build(
            Running(j => { j.ProgressKeysDone = 2; j.ProgressKeysTotal = 10; }), null, Start.AddSeconds(100), Threshold)!;
        Assert.Equal(400, v.EtaSeconds!.Value, 3); // 100s for 2 keys -> 8 left
        Assert.True(v.EtaIsEstimate);
        Assert.True(v.TotalIsEstimate);
    }

    [Fact]
    public void SeenBeyondTotal_EtaZero()
    {
        var v = ScrapeProgressEstimator.Build(Running(j => j.ProgressTotal = 500), null, Start.AddSeconds(100), Threshold)!;
        Assert.Equal(0, v.EtaSeconds);
    }

    [Fact]
    public void NotRunning_NoProgressView_AndNeverStalled()
    {
        var done = Running(j => { j.Status = ScrapingJobStatus.Completed; j.LastHeartbeatAt = Start; });
        Assert.Null(ScrapeProgressEstimator.Build(done, 100, Start.AddHours(5), Threshold));
        Assert.False(ScrapeProgressEstimator.IsStalled(done, Start.AddHours(5), Threshold));
    }

    [Fact]
    public void Stalled_FollowsHeartbeat_NotPageProgress()
    {
        // Page progress is hours old but the heartbeat is fresh: not stalled.
        var alive = Running(j => { j.LastProgressAt = Start; j.LastHeartbeatAt = Start.AddHours(1).AddSeconds(-10); });
        Assert.False(ScrapeProgressEstimator.IsStalled(alive, Start.AddHours(1), Threshold));

        var dead = Running(j => j.LastHeartbeatAt = Start.AddSeconds(100));
        Assert.True(ScrapeProgressEstimator.IsStalled(dead, Start.AddSeconds(161), Threshold));
        Assert.False(ScrapeProgressEstimator.IsStalled(dead, Start.AddSeconds(159), Threshold));
    }

    [Fact]
    public void Stalled_UsesStartedAtWhenNoHeartbeatYet()
    {
        var j = Running(x => x.LastHeartbeatAt = null);
        Assert.False(ScrapeProgressEstimator.IsStalled(j, Start.AddSeconds(30), Threshold));
        Assert.True(ScrapeProgressEstimator.IsStalled(j, Start.AddSeconds(90), Threshold));
    }

    [Fact]
    public void PreviousHealthyCount_PicksLatestHealthyOther()
    {
        var cur = Running();
        var jobs = new[]
        {
            cur,
            new ScrapingJob { Id = Guid.NewGuid(), Status = ScrapingJobStatus.Completed, StartedAt = Start.AddDays(-2), ProductsScraped = 10 },
            new ScrapingJob { Id = Guid.NewGuid(), Status = ScrapingJobStatus.Completed, StartedAt = Start.AddDays(-1), ProductsScraped = 20 },
            new ScrapingJob { Id = Guid.NewGuid(), Status = ScrapingJobStatus.Failed, StartedAt = Start.AddHours(-1), ProductsScraped = 0 },
        };
        Assert.Equal(20, ScrapeProgressEstimator.PreviousHealthyCount(jobs, cur.Id));
    }

    [Fact]
    public void StatusDto_DeserializesProgressFields()
    {
        const string json = """
            {"chainSlug":"continente","isScheduled":true,"lastStatus":1,"productsScraped":0,
             "progress":{"productsSeen":12400,"total":58000,"totalIsEstimate":true,"etaSeconds":3600.5,
                         "etaIsEstimate":true,"stalled":true,"key":"a","keysDone":1,"keysTotal":9}}
            """;
        var dto = JsonSerializer.Deserialize<ScrapingStatusDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(58000, dto.Progress!.Total);
        Assert.True(dto.Progress.TotalIsEstimate);
        Assert.True(dto.Progress.Stalled);
        Assert.Null(JsonSerializer.Deserialize<ScrapingStatusDto>(
            """{"chainSlug":"x","isScheduled":false,"productsScraped":0}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Progress);
    }
}

public class BaseScraperProgressTests
{
    private sealed class Probe : BaseHttpScraper
    {
        public Probe() : base(Substitute.For<IHttpClientFactory>(), Substitute.For<ILogger>(), "probe") { }
        public override string StoreChainSlug => "probe";
        public override Task<ScrapeRunResult> ScrapeProductsAsync(string? category = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public override Task<IReadOnlyList<ScrapedStoreLocation>> ScrapeStoreLocationsAsync(CancellationToken ct = default)
            => throw new NotImplementedException();
        public void Begin() => BeginRun();
        public void Keys(int n) => SetKeysTotal(n);
        public void Page(string key, int seen, int? total = null) => ReportProgress(key, 0, seen, total);
        public void Done(string key, int seen) => ReportKeyDone(key, seen);
    }

    [Fact]
    public void PagesAreThrottled_KeyCompletionAlwaysEmits_CountersCarried()
    {
        var p = new Probe { ProgressInterval = TimeSpan.FromHours(1) };
        var got = new List<ScrapeProgress>();
        p.ProgressSink = got.Add;
        p.Begin();
        p.Keys(3);
        p.Page("a", 10, 100);
        p.Page("a", 20, 100); // throttled
        p.Done("a", 20);
        p.Page("b", 30);      // throttled
        p.Done("b", 30);

        Assert.Equal(3, got.Count);
        Assert.Equal(10, got[0].ProductsSeen);
        Assert.Equal(100, got[0].KeySiteTotal);
        Assert.Equal((1, 3), (got[1].KeysDone, got[1].KeysTotal));
        Assert.Equal((2, 3), (got[2].KeysDone, got[2].KeysTotal));
    }

    [Fact]
    public void SinkException_IsSwallowed()
    {
        var p = new Probe();
        p.ProgressSink = _ => throw new InvalidOperationException("db down");
        p.Begin();
        p.Page("a", 1);
        p.Done("a", 1);
    }
}

public sealed class ScrapeProgressJobTests : IAsyncLifetime
{
    private static readonly Guid ChainId = Guid.NewGuid();
    private readonly string _dbName = $"ProgressJob_{Guid.NewGuid()}";
    private ServiceProvider _sp = default!;
    private SavvoriDbContext _db = default!;
    private ScrapingTelemetry _telemetry = default!;

    public async ValueTask InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddDbContext<SavvoriDbContext>(o => o.UseInMemoryDatabase(_dbName));
        _sp = services.BuildServiceProvider();
        _db = _sp.CreateScope().ServiceProvider.GetRequiredService<SavvoriDbContext>();
        _db.StoreChains.Add(new StoreChain
        {
            Id = ChainId, Name = "Pingo Doce", Slug = "pingodoce", BaseUrl = "https://www.pingodoce.pt", IsActive = true
        });
        await _db.SaveChangesAsync();
        await CategorySeeder.SeedAsync(_db);
        _telemetry = new ScrapingTelemetry();
    }

    public async ValueTask DisposeAsync()
    {
        _telemetry.Dispose();
        await _db.DisposeAsync();
        await _sp.DisposeAsync();
    }

    private sealed class BlockingScraper(Task gate) : IStoreScraper
    {
        public string StoreChainSlug => "pingodoce";
        public async Task<ScrapeRunResult> ScrapeProductsAsync(string? category = null, CancellationToken ct = default)
        {
            await gate;
            return new ScrapeRunResult([], [], []);
        }
        public Task<CanaryResult> ProbeAsync(CancellationToken ct = default) => Task.FromResult(new CanaryResult(true, "ok", 0));
        public Task<IReadOnlyList<ScrapedStoreLocation>> ScrapeStoreLocationsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ScrapedStoreLocation>>([]);
    }

    [Fact]
    public async Task Heartbeat_AdvancesWhileNoPageProgress_AndColumnsNullOnCompletion()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new TaskCompletionSource();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Scraping:HeartbeatSeconds"] = "1" })
            .Build();
        var job = new StoreScrapeJob(
            [new BlockingScraper(gate.Task)],
            new ScraperResultProcessor(_db, NullLogger<ScraperResultProcessor>.Instance),
            _db, _telemetry, NullLogger<StoreScrapeJob>.Instance, config,
            _sp.GetRequiredService<IServiceScopeFactory>());

        var context = Substitute.For<IJobExecutionContext>();
        context.MergedJobDataMap.Returns(new JobDataMap
        {
            { StoreScrapeJob.StoreChainSlugKey, "pingodoce" },
            { StoreScrapeJob.ScrapeLocationsKey, false }
        });
        context.CancellationToken.Returns(ct);

        var run = Task.Run(() => job.Execute(context, CancellationToken.None).AsTask(), ct);

        async Task<DateTime?> Beat()
        {
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SavvoriDbContext>();
            return await db.ScrapingJobs.AsNoTracking().Select(j => j.LastHeartbeatAt).FirstOrDefaultAsync(ct);
        }

        DateTime? first = null;
        for (var i = 0; i < 50 && first is null; i++) { await Task.Delay(100, ct); first = await Beat(); }
        Assert.NotNull(first);

        DateTime? later = first;
        for (var i = 0; i < 60 && later == first; i++) { await Task.Delay(100, ct); later = await Beat(); }
        Assert.True(later > first, "heartbeat should advance with no page progress");

        using (var scope = _sp.CreateScope())
        {
            var running = await scope.ServiceProvider.GetRequiredService<SavvoriDbContext>()
                .ScrapingJobs.AsNoTracking().SingleAsync(ct);
            Assert.Equal(ScrapingJobStatus.Running, running.Status);
            Assert.Null(running.LastProgressAt);
        }

        gate.SetResult();
        await run;

        using var scope2 = _sp.CreateScope();
        var done = await scope2.ServiceProvider.GetRequiredService<SavvoriDbContext>()
            .ScrapingJobs.AsNoTracking().SingleAsync(ct);
        Assert.NotEqual(ScrapingJobStatus.Running, done.Status);
        Assert.Null(done.LastHeartbeatAt);
        Assert.Null(done.LastProgressAt);
        Assert.Null(done.ProgressProductsSeen);
        Assert.Null(done.ProgressKey);
    }

    [Fact]
    public async Task Writer_ReportPersistsSnapshot_AndFailuresAreSwallowed()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        _db.ScrapingJobs.Add(new ScrapingJob { Id = id, StoreChainId = ChainId, Status = ScrapingJobStatus.Running, StartedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync(ct);

        await using var w = new ScrapeProgressWriter(_sp.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, id, TimeSpan.FromHours(1));
        w.Report(new ScrapeProgress(42, "cat", 24, 100, 0, 1));
        for (var i = 0; i < 50; i++)
        {
            await Task.Delay(50, ct);
            using var s = _sp.CreateScope();
            var j = await s.ServiceProvider.GetRequiredService<SavvoriDbContext>().ScrapingJobs.AsNoTracking().SingleAsync(ct);
            if (j.ProgressProductsSeen is null) continue;
            Assert.Equal(42, j.ProgressProductsSeen);
            Assert.Equal(100, j.ProgressTotal); // single key: declared total kept
            Assert.NotNull(j.LastProgressAt);
            break;
        }

        // Unknown job and a broken scope factory must not throw.
        await using var ghost = new ScrapeProgressWriter(_sp.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, Guid.NewGuid(), TimeSpan.FromHours(1));
        ghost.Report(new ScrapeProgress(1, "x", 0, null, 0, 1));
        var broken = Substitute.For<IServiceScopeFactory>();
        broken.CreateScope().Returns(_ => throw new InvalidOperationException("boom"));
        await using var bad = new ScrapeProgressWriter(broken, NullLogger.Instance, id, TimeSpan.FromHours(1));
        bad.Report(new ScrapeProgress(1, "x", 0, null, 0, 1));
        await bad.MarkProcessingAsync();
        await bad.StopAndClearAsync();
    }
}
