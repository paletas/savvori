using System.Net;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Savvori.Shared;
using Savvori.WebApi.Scraping;
using Savvori.WebApi.Scraping.Scrapers;

namespace Savvori.Web.Tests;

public class ReattemptTests
{
    [Fact]
    public async Task AuchanRootFirstPageFails_RecoveredAtEnd()
    {
        var html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "auchan_products.html"));
        var handler = new FakeHttpMessageHandler();
        handler.SetDefaultResponse(html);
        handler.SetupRouteSequence("cgid=root&start=0", (HttpStatusCode.Gone, "gone"), (HttpStatusCode.OK, html));

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("auchan").Returns(new HttpClient(handler));
        var scraper = new AuchanScraper(factory, Substitute.For<ILogger<AuchanScraper>>())
        {
            ReattemptDelay = TimeSpan.Zero,
            RequestSpacing = TimeSpan.Zero
        };

        var result = await scraper.ScrapeProductsAsync(null, CancellationToken.None);

        var root = Assert.Single(result.Outcomes, o => o.Key == "root");
        Assert.Equal(OutcomeResult.Recovered, root.Result);
        Assert.Equal(4, result.Products.Count);
        Assert.Equal(ScrapingJobStatus.CompletedWithWarnings, ScrapeHealthEvaluator.Evaluate(result).Status);
    }
}
