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

    [Fact]
    public void LinuxNanosecondClock_KeepsEmittingAcrossTheOldOverflowPoint()
    {
        // Stopwatch.Frequency is 1e9 on Linux: the old `ts * TicksPerSecond / Frequency` overflowed a long once
        // ts passed 2^63 / 1e7 ns (~922 s of uptime) and then wrapped every ~1844.67 s, gating emits for ~30 min.
        const long freq = 1_000_000_000;
        long ts = (long.MaxValue / TimeSpan.TicksPerSecond) - 5 * freq; // 5 s before the old wrap point
        Assert.True(OldClock(ts, freq) > 0); // sanity: still fine here
        var t = new ProgressThrottle(TimeSpan.FromSeconds(1), () => ts, freq);
        var emitted = 0;
        for (var i = 0; i < 40; i++)           // 40 s of one-per-second pages straddling the wrap
        {
            if (t.ShouldEmit()) emitted++;
            ts += freq;
        }
        Assert.Equal(40, emitted);
        Assert.True(OldClock(ts, freq) < OldClock(ts - 40 * freq, freq)); // the old formula really went backwards

        static long OldClock(long raw, long f) => unchecked(raw * TimeSpan.TicksPerSecond / f);
    }

    [Fact]
    public void LinuxNanosecondClock_FarFromZero_StillGatesWithinInterval()
    {
        const long freq = 1_000_000_000;
        long ts = 4_000_000_000_000_000_000; // ~46 days of uptime
        var t = new ProgressThrottle(TimeSpan.FromSeconds(15), () => ts, freq);
        Assert.True(t.ShouldEmit());
        ts += 14 * freq;
        Assert.False(t.ShouldEmit());
        ts += 1 * freq;
        Assert.True(t.ShouldEmit());
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

    private static ScrapingJob MultiKey(Action<ScrapingJob>? tweak = null) => Running(j =>
    {
        j.ProgressKeysTotal = 4;
        j.ProgressKeysDone = 1;
        j.ProgressDoneKeys = "[\"a\"]";
        j.ProgressKey = "b";
        j.ProgressPagesDone = 12;   // a: 10 pages, b: 2 so far
        j.ProgressKeyPages = 2;
        tweak?.Invoke(j);
    });

    private static readonly DateTime Now = Start.AddSeconds(120); // 10 s per page

    [Fact]
    public void MultiKey_WithHistory_WeighsRemainingKeysByLastRunPages()
    {
        var hist = new Dictionary<string, int> { ["a"] = 10, ["b"] = 8, ["c"] = 100, ["d"] = 1 };
        var v = ScrapeProgressEstimator.Build(MultiKey(), null, Now, Threshold, hist)!;
        // current key remainder 8-2=6, plus c and d = 101 -> 107 pages x 10 s
        Assert.Equal(1070, v.EtaSeconds!.Value, 3);
        Assert.True(v.EtaIsEstimate);
        Assert.True(v.TotalIsEstimate);
    }

    [Fact]
    public void MultiKey_NoHistory_UsesDeclaredCurrentKeyThenFinishedKeyAverage()
    {
        var v = ScrapeProgressEstimator.Build(MultiKey(j => j.ProgressKeyPagesTotal = 5), null, Now, Threshold)!;
        // current: 5-2=3; two unknown keys at the finished-key average (10) = 20 -> 23 pages
        Assert.Equal(230, v.EtaSeconds!.Value, 3);
    }

    [Fact]
    public void MultiKey_NoHistoryNoDeclared_CurrentKeyAssumedAverage()
    {
        var v = ScrapeProgressEstimator.Build(MultiKey(), null, Now, Threshold)!;
        // current: avg 10 - 2 = 8; two keys x 10 = 20 -> 28 pages
        Assert.Equal(280, v.EtaSeconds!.Value, 3);
    }

    [Fact]
    public void MultiKey_ContentPageKeysOfOnePage_AreNotInflated()
    {
        var hist = new Dictionary<string, int> { ["a"] = 10, ["b"] = 3, ["c"] = 1, ["d"] = 1 };
        var v = ScrapeProgressEstimator.Build(MultiKey(), null, Now, Threshold, hist)!;
        Assert.Equal(30, v.EtaSeconds!.Value, 3); // (3-2) + 1 + 1 = 3 pages
    }

    [Fact]
    public void MultiKey_CurrentKeyPastHistory_RemainderIsZeroNotNegative()
    {
        var hist = new Dictionary<string, int> { ["a"] = 10, ["b"] = 1, ["c"] = 4, ["d"] = 4 };
        var v = ScrapeProgressEstimator.Build(MultiKey(), null, Now, Threshold, hist)!;
        Assert.Equal(80, v.EtaSeconds!.Value, 3); // 0 + 4 + 4
    }

    [Fact]
    public void MultiKey_ElapsedAnchoredToNow_NotLastProgressAt()
    {
        var job = MultiKey(j => j.LastProgressAt = Start.AddSeconds(30));
        var v = ScrapeProgressEstimator.Build(job, null, Now, Threshold)!;
        Assert.Equal(280, v.EtaSeconds!.Value, 3);
    }

    [Fact]
    public void SingleKeyDeclaredTotal_UsesOffsetOverTotal()
    {
        var v = ScrapeProgressEstimator.Build(
            Running(j => { j.ProgressTotal = 2000; j.ProgressOffset = 500; j.ProgressKeysTotal = 1; j.ProgressPagesDone = 21; }),
            null, Start.AddSeconds(100), Threshold)!;
        Assert.Equal(300, v.EtaSeconds!.Value, 3); // 500 in 100 s -> 1500 left
        Assert.False(v.EtaIsEstimate);
        Assert.False(v.TotalIsEstimate);
    }

    [Fact]
    public void MultiKey_NothingKnown_EtaNull()
    {
        var job = MultiKey(j => { j.ProgressKeysDone = 0; j.ProgressDoneKeys = null; j.ProgressPagesDone = 2; });
        Assert.Null(ScrapeProgressEstimator.Build(job, null, Now, Threshold)!.EtaSeconds);
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
        public void Page(string key, int seen, int? total = null, int? step = null) => ReportProgress(key, 0, seen, total, step);
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
    public void PageCounters_CarriedPerKey_WithDoneKeysAndExpectedPages()
    {
        var p = new Probe { ProgressInterval = TimeSpan.Zero };
        var got = new List<ScrapeProgress>();
        p.ProgressSink = got.Add;
        p.Begin();
        p.Keys(2);
        p.Page("a", 10, 100, 48);
        p.Page("a", 20, 100, 48);
        p.Done("a", 20);
        p.Page("b", 30);

        Assert.Equal(3, got[0].KeyPagesTotal); // ceil(100/48)
        Assert.Equal((2, 2), (got[1].PagesDone, got[1].KeyPages));
        Assert.Equal(["a"], got[2].DoneKeys);
        Assert.Equal((3, 1, null), (got[3].PagesDone, got[3].KeyPages, got[3].KeyPagesTotal));
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
