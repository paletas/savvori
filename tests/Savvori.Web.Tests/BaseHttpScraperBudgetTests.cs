using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Savvori.Shared;
using Savvori.WebApi.Scraping;

namespace Savvori.Web.Tests;

public class BaseHttpScraperBudgetTests
{
    internal sealed class TestScraper : BaseHttpScraper
    {
        public TestScraper(IHttpClientFactory factory)
            : base(factory, Substitute.For<ILogger>(), "test", retryBaseDelay: TimeSpan.FromMilliseconds(1))
        {
            ReattemptDelay = TimeSpan.Zero;
            RequestSpacing = TimeSpan.Zero;
        }

        public override string StoreChainSlug => "test";

        public override Task<ScrapeRunResult> ScrapeProductsAsync(string? category = null, CancellationToken ct = default)
            => throw new NotImplementedException();

        public override Task<IReadOnlyList<ScrapedStoreLocation>> ScrapeStoreLocationsAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<string?> Str(string url, ScrapeOutcome o, CancellationToken ct = default) => GetStringAsync(url, o, ct);
        public Task Reattempt(List<ScrapeOutcome> outcomes, Func<ScrapeOutcome, Task<ScrapeOutcome>> rerun)
            => ReattemptAsync(outcomes, rerun, default);
        public void Begin() => BeginRun();
        public bool IsHalted => Halted;
        public bool IsIncomplete => Incomplete;
        public int Used => RequestsUsed;
        public ScrapeRunResult Build(IReadOnlyList<ScrapedProduct> p, IReadOnlyList<ScrapeOutcome> o, IReadOnlyList<string> w)
            => BuildResult(p, o, w);
        public void Loop(PagingGuard guard, List<string> w) => ReportLoop(guard, w);
    }

    internal static (TestScraper Scraper, FakeHttpMessageHandler Handler) Create()
    {
        var handler = new FakeHttpMessageHandler();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("test").Returns(new HttpClient(handler));
        return (new TestScraper(factory), handler);
    }

    private const string Url = "https://test.example/cat/page";

    [Fact]
    public async Task Spacing_WaitsBetweenRequests()
    {
        var (s, h) = Create();
        h.SetupRoute("cat/page", "ok");
        s.RequestSpacing = TimeSpan.FromMilliseconds(200);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 3; i++) Assert.Equal("ok", await s.Str(Url, new ScrapeOutcome()));
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds >= 350, $"elapsed {sw.ElapsedMilliseconds}ms");
        Assert.Equal(3, h.RequestCount);
    }

    [Fact]
    public async Task StopOn403_NoRetry_HaltsRun()
    {
        var (s, h) = Create();
        h.SetupRoute("cat/page", "forbidden", HttpStatusCode.Forbidden);
        var o = new ScrapeOutcome();
        Assert.Null(await s.Str(Url, o));
        Assert.Equal(1, h.RequestCount);
        Assert.Equal(OutcomeResult.Blocked, o.Result);
        Assert.True(s.IsHalted);
        Assert.True(s.IsIncomplete);
        Assert.Null(await s.Str(Url, new ScrapeOutcome()));
        Assert.Equal(1, h.RequestCount);
    }

    [Fact]
    public async Task StopOn429_NoRetry()
    {
        var (s, h) = Create();
        h.SetupRoute("cat/page", "slow down", HttpStatusCode.TooManyRequests);
        var o = new ScrapeOutcome();
        Assert.Null(await s.Str(Url, o));
        Assert.Equal(1, h.RequestCount);
        Assert.Equal(OutcomeResult.Blocked, o.Result);
        Assert.True(s.IsHalted);
        Assert.Null(await s.Str(Url, new ScrapeOutcome()));
        Assert.Equal(1, h.RequestCount);
    }

    [Fact]
    public async Task NoReattemptWhenHalted()
    {
        var (s, h) = Create();
        h.SetupRoute("cat/page", "forbidden", HttpStatusCode.Forbidden);
        var o = new ScrapeOutcome { Key = "k" };
        await s.Str(Url, o);
        var calls = 0;
        await s.Reattempt([o], x => { calls++; return Task.FromResult(x); });
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Bound_RequestCeiling_SendsNoExtraRequest()
    {
        var (s, h) = Create();
        h.SetupRoute("cat/page", "ok");
        s.RequestCeiling = 2;
        s.Begin();
        var o = new ScrapeOutcome();
        Assert.Equal("ok", await s.Str(Url, o));
        Assert.Equal("ok", await s.Str(Url, o));
        Assert.Null(await s.Str(Url, o));
        Assert.Equal(2, h.RequestCount);
        Assert.Equal(OutcomeResult.Ok, o.Result);
        Assert.True(s.IsHalted);
        Assert.True(s.IsIncomplete);
        Assert.Contains("Bound reached: request ceiling 2 after 2 requests; run ended early", s.Build([], [], []).Warnings);
    }

    [Fact]
    public async Task Bound_WallClock_HaltsRun()
    {
        var (s, h) = Create();
        h.SetupRoute("cat/page", "ok");
        s.WallClockLimit = TimeSpan.Zero;
        s.Begin();
        Assert.Null(await s.Str(Url, new ScrapeOutcome()));
        Assert.Equal(0, h.RequestCount);
        Assert.True(s.IsHalted);
        Assert.Contains(s.Build([], [], []).Warnings, x => x.StartsWith("Bound reached: wall clock 0h"));
    }

    [Fact]
    public async Task BeginRun_ResetsHalt()
    {
        var (s, h) = Create();
        h.SetupRoute("cat/page", "ok");
        s.RequestCeiling = 0;
        s.Begin();
        Assert.Null(await s.Str(Url, new ScrapeOutcome()));
        Assert.True(s.IsHalted);
        s.RequestCeiling = 100;
        s.WallClockLimit = TimeSpan.FromHours(1);
        s.Begin();
        Assert.False(s.IsHalted);
        Assert.False(s.IsIncomplete);
        Assert.Equal("ok", await s.Str(Url, new ScrapeOutcome()));
        Assert.Equal(1, s.Used);
    }
}
