using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;
using Savvori.Shared;
using Savvori.WebApi;
using Savvori.WebApi.Scraping;
using Savvori.WebApi.Scraping.Scrapers;

namespace Savvori.Web.Tests;

public sealed class CanaryTests : IAsyncLifetime
{
    private SavvoriDbContext _db = default!;
    private ScrapingTelemetry _telemetry = default!;
    private FakeHttpMessageHandler _handler = default!;
    private StoreScrapeJob _job = default!;

    public async ValueTask InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<SavvoriDbContext>()
            .UseInMemoryDatabase($"CanaryTests_{Guid.NewGuid()}")
            .Options;
        _db = new SavvoriDbContext(options);
        _db.StoreChains.Add(new StoreChain
        {
            Id = Guid.NewGuid(), Name = "Auchan", Slug = "auchan", BaseUrl = "https://www.auchan.pt", IsActive = true
        });
        await _db.SaveChangesAsync();
        await CategorySeeder.SeedAsync(_db);

        _handler = new FakeHttpMessageHandler();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("auchan").Returns(new HttpClient(_handler));
        var scraper = new AuchanScraper(factory, Substitute.For<ILogger<AuchanScraper>>())
        {
            ReattemptDelay = TimeSpan.Zero,
            RequestSpacing = TimeSpan.Zero
        };

        _telemetry = new ScrapingTelemetry();
        var processor = new ScraperResultProcessor(_db, NullLogger<ScraperResultProcessor>.Instance);
        _job = new StoreScrapeJob([scraper], processor, _db, _telemetry, NullLogger<StoreScrapeJob>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        _telemetry.Dispose();
        await _db.DisposeAsync();
    }

    private static IJobExecutionContext MakeContext(CancellationToken ct)
    {
        var context = Substitute.For<IJobExecutionContext>();
        context.MergedJobDataMap.Returns(new JobDataMap
        {
            { StoreScrapeJob.StoreChainSlugKey, "auchan" },
            { StoreScrapeJob.ScrapeLocationsKey, false }
        });
        context.CancellationToken.Returns(ct);
        return context;
    }

    [Fact]
    public async Task NoTiles_SkipsScrape_Degraded_AtMost2Requests()
    {
        var ct = TestContext.Current.CancellationToken;
        await _job.Execute(MakeContext(ct), CancellationToken.None);

        var saved = await _db.ScrapingJobs.AsNoTracking().SingleAsync(ct);
        Assert.Equal(ScrapingJobStatus.Degraded, saved.Status);
        Assert.StartsWith("Canary:", saved.HealthReason);
        Assert.True(_handler.RequestCount <= 2);
        var rows = await _db.ScrapingRunOutcomes.AsNoTracking().ToListAsync(ct);
        Assert.Single(rows);
        Assert.Equal(OutcomeKind.Canary, rows[0].Kind);
        Assert.Empty(await _db.StoreProducts.AsNoTracking().ToListAsync(ct));
    }

    [Fact]
    public async Task Status410_SkipsScrape()
    {
        var ct = TestContext.Current.CancellationToken;
        _handler.SetupRoute("pesquisa", "gone", HttpStatusCode.Gone);
        await _job.Execute(MakeContext(ct), CancellationToken.None);

        var saved = await _db.ScrapingJobs.AsNoTracking().SingleAsync(ct);
        Assert.Equal(ScrapingJobStatus.Degraded, saved.Status);
        Assert.Equal(2, _handler.RequestCount);
    }
}
