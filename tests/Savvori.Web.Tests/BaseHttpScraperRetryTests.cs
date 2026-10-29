using System.Net;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Savvori.Shared;
using Savvori.WebApi.Scraping;
using AngleSharp.Dom;

namespace Savvori.Web.Tests;

public class BaseHttpScraperRetryTests
{
    private const string Url = "https://test.example/cat/page";

    private sealed class TestScraper : BaseHttpScraper
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

        public Task<IDocument?> Html(string url, ScrapeOutcome o, CancellationToken ct = default) => GetHtmlAsync(url, o, ct);
        public Task<CanaryResult> ProbeHtml(string url, Func<IDocument, (int, int)> inspect) => ProbeHtmlAsync(url, inspect, default);
        public Task<CanaryResult> ProbeStr(string url, Func<string, (int, int)> inspect) => ProbeStringAsync(url, inspect, default);
        public Task Reattempt(List<ScrapeOutcome> outcomes, Func<ScrapeOutcome, Task<ScrapeOutcome>> rerun)
            => ReattemptAsync(outcomes, rerun, default);
        public Task<string?> Str(string url, ScrapeOutcome o, CancellationToken ct = default) => GetStringAsync(url, o, ct);
    }

    private static (TestScraper Scraper, FakeHttpMessageHandler Handler) Create()
    {
        var handler = new FakeHttpMessageHandler();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("test").Returns(new HttpClient(handler));
        return (new TestScraper(factory), handler);
    }

    [Fact]
    public async Task StopOn403_NoRetry()
    {
        var (s, h) = Create();
        h.SetupRoute("cat/page", "forbidden", HttpStatusCode.Forbidden);
        var o = new ScrapeOutcome();
        var doc = await s.Html(Url, o);
        Assert.Null(doc);
        Assert.Equal(1, h.RequestCount);
        Assert.Equal(OutcomeResult.Blocked, o.Result);
        Assert.Equal(1, o.StatusCounts[403]);
        Assert.Equal(0, o.Retries);
    }

    [Fact]
    public async Task RetriesEmpty200()
    {
        var (s, h) = Create();
        h.SetupRoute("cat/page", "");
        var o = new ScrapeOutcome();
        var doc = await s.Html(Url, o);
        Assert.Null(doc);
        Assert.Equal(4, h.RequestCount);
        Assert.Equal(OutcomeResult.Failed, o.Result);
    }

    [Fact]
    public async Task NoRetry410_Failed()
    {
        var (s, h) = Create();
        h.SetupRoute("cat/page", "gone", HttpStatusCode.Gone);
        var o = new ScrapeOutcome();
        var doc = await s.Html(Url, o);
        Assert.Null(doc);
        Assert.Equal(1, h.RequestCount);
        Assert.Equal(OutcomeResult.Failed, o.Result);
        Assert.Equal(1, o.StatusCounts[410]);
        Assert.Equal(0, o.Retries);
    }

    [Fact]
    public async Task Redirect_Recorded()
    {
        var (s, h) = Create();
        h.SetupRoute("cat/page", "<html><body>home</body></html>");
        h.SetupRedirect("cat/page", "https://test.example/");
        var o = new ScrapeOutcome();
        var doc = await s.Html(Url, o);
        Assert.Null(doc);
        Assert.Equal(OutcomeResult.Redirected, o.Result);
    }

    [Fact]
    public async Task ProbeHtml_Ok()
    {
        var (s, h) = Create();
        h.SetupRoute("cat/page", "<html><body>x</body></html>");
        var r = await s.ProbeHtml(Url, _ => (2, 2));
        Assert.True(r.Ok);
        Assert.Equal(1, h.RequestCount);
    }

    [Fact]
    public async Task ProbeHtml_410_NotOk()
    {
        var (s, h) = Create();
        h.SetupRoute("cat/page", "gone", HttpStatusCode.Gone);
        var r = await s.ProbeHtml(Url, _ => (2, 2));
        Assert.False(r.Ok);
        Assert.Contains(Url, r.Message);
        Assert.Contains("Failed", r.Message);
        Assert.Equal(2, h.RequestCount); // canary retries any non-Blocked failure once
    }

    private static (HttpStatusCode, string)[] Fail500(int n) =>
        Enumerable.Repeat((HttpStatusCode.InternalServerError, "err"), n).ToArray();

    [Fact]
    public async Task ProbeHtml_FailedRequest_RetriesOnceAndPasses()
    {
        var (s, h) = Create();
        // First attempt exhausts its 4 tries, the canary retry gets a good page.
        h.SetupRouteSequence("cat/page", [.. Fail500(4), (HttpStatusCode.OK, "<html><body>x</body></html>")]);
        var r = await s.ProbeHtml(Url, _ => (2, 2));
        Assert.True(r.Ok);
        Assert.Equal(5, r.Requests);
        Assert.Equal(5, h.RequestCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task ProbeHtml_Blocked_NotRetried(HttpStatusCode status)
    {
        var (s, h) = Create();
        h.SetupRoute("cat/page", "blocked", status);
        var r = await s.ProbeHtml(Url, _ => (2, 2));
        Assert.False(r.Ok);
        Assert.Equal(1, r.Requests);
        Assert.Equal(1, h.RequestCount);
        Assert.DoesNotContain("retry also failed", r.Message);
    }

    [Fact]
    public async Task ProbeStr_TwoFailures_FailsWithSummedRequests()
    {
        var (s, h) = Create();
        h.SetupRoute("cat/page", "err", HttpStatusCode.InternalServerError);
        var r = await s.ProbeStr(Url, _ => (2, 2));
        Assert.False(r.Ok);
        Assert.Equal(8, r.Requests);
        Assert.Equal(8, h.RequestCount);
        Assert.Contains("retry also failed", r.Message);
    }

    [Fact]
    public async Task ProbeHtml_NoTiles_NotOk()
    {
        var (s, h) = Create();
        h.SetupRoute("cat/page", "<html><body>x</body></html>");
        var r = await s.ProbeHtml(Url, _ => (0, 0));
        Assert.False(r.Ok);
        Assert.Contains("no product tiles", r.Message);
    }

    [Fact]
    public async Task ProbeHtml_MissingFields_NotOk()
    {
        var (s, h) = Create();
        h.SetupRoute("cat/page", "<html><body>x</body></html>");
        var r = await s.ProbeHtml(Url, _ => (3, 0));
        Assert.False(r.Ok);
        Assert.Contains("missing name, price or id", r.Message);
    }

    [Fact]
    public async Task Reattempt_Failed_BecomesRecovered()
    {
        var (s, _) = Create();
        var old = new ScrapeOutcome { Key = "k", Result = OutcomeResult.Failed, FirstError = "HTTP 410" };
        var list = new List<ScrapeOutcome> { old };
        await s.Reattempt(list, _ => Task.FromResult(new ScrapeOutcome { Key = "k" }));
        Assert.Single(list);
        Assert.Equal(OutcomeResult.Recovered, list[0].Result);
        Assert.Equal("HTTP 410", list[0].FirstError);
        Assert.NotSame(old, list[0]);
    }

    [Fact]
    public async Task Reattempt_StillFailing_KeepsOriginal()
    {
        var (s, _) = Create();
        var old = new ScrapeOutcome { Key = "k", Result = OutcomeResult.Failed };
        var list = new List<ScrapeOutcome> { old };
        await s.Reattempt(list, _ => Task.FromResult(new ScrapeOutcome { Key = "k", Result = OutcomeResult.Failed }));
        Assert.Same(old, list[0]);
    }

    [Fact]
    public async Task Reattempt_AllOk_NoRerun()
    {
        var (s, _) = Create();
        var list = new List<ScrapeOutcome> { new() { Key = "k" } };
        var called = false;
        await s.Reattempt(list, o => { called = true; return Task.FromResult(o); });
        Assert.False(called);
    }

    [Theory]
    [InlineData("<html><body>Just a moment...</body></html>")]
    [InlineData("<html><body><div id=\"px-captcha\"></div></body></html>")]
    public async Task BotMarker_Blocked(string body)
    {
        var (s, h) = Create();
        h.SetupRoute("cat/page", body);
        var o = new ScrapeOutcome();
        var doc = await s.Html(Url, o);
        Assert.Null(doc);
        Assert.Equal(OutcomeResult.Blocked, o.Result);
    }
}
