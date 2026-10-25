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
            : base(factory, Substitute.For<ILogger>(), "test", retryBaseDelay: TimeSpan.FromMilliseconds(1)) { }

        public override string StoreChainSlug => "test";

        public override Task<ScrapeRunResult> ScrapeProductsAsync(string? category = null, CancellationToken ct = default)
            => throw new NotImplementedException();

        public override Task<IReadOnlyList<ScrapedStoreLocation>> ScrapeStoreLocationsAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IDocument?> Html(string url, ScrapeOutcome o, CancellationToken ct = default) => GetHtmlAsync(url, o, ct);
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
    public async Task Retries403_Bounded()
    {
        var (s, h) = Create();
        h.SetupRoute("cat/page", "forbidden", HttpStatusCode.Forbidden);
        var o = new ScrapeOutcome();
        var doc = await s.Html(Url, o);
        Assert.Null(doc);
        Assert.Equal(4, h.RequestCount);
        Assert.Equal(OutcomeResult.Blocked, o.Result);
        Assert.Equal(4, o.StatusCounts[403]);
        Assert.Equal(3, o.Retries);
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
