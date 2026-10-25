using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;
using Savvori.Shared;
using Savvori.WebApi;
using Savvori.WebApi.Scraping;

namespace Savvori.Web.Tests;

public sealed class StoreScrapeJobTests : IAsyncLifetime
{
    private static readonly Guid ChainId = Guid.NewGuid();

    private SavvoriDbContext _db = default!;
    private ScrapingTelemetry _telemetry = default!;
    private StoreScrapeJob _job = default!;
    private ScraperResultProcessor _processor = default!;

    public async ValueTask InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<SavvoriDbContext>()
            .UseInMemoryDatabase($"StoreScrapeJobTests_{Guid.NewGuid()}")
            .Options;

        _db = new SavvoriDbContext(options);
        _db.StoreChains.Add(new StoreChain
        {
            Id = ChainId,
            Name = "Pingo Doce",
            Slug = "pingodoce",
            BaseUrl = "https://www.pingodoce.pt",
            IsActive = true
        });
        await _db.SaveChangesAsync();

        await CategorySeeder.SeedAsync(_db);

        _processor = new ScraperResultProcessor(_db, NullLogger<ScraperResultProcessor>.Instance);
        _telemetry = new ScrapingTelemetry();

        _job = MakeJob(new FakeStoreScraper());
    }

    private StoreScrapeJob MakeJob(FakeStoreScraper scraper) =>
        new([scraper], _processor, _db, _telemetry, NullLogger<StoreScrapeJob>.Instance);

    private static IJobExecutionContext MakeContext(CancellationToken ct)
    {
        var context = Substitute.For<IJobExecutionContext>();
        context.MergedJobDataMap.Returns(new JobDataMap
        {
            { StoreScrapeJob.StoreChainSlugKey, "pingodoce" },
            { StoreScrapeJob.ScrapeLocationsKey, false }
        });
        context.CancellationToken.Returns(ct);
        return context;
    }

    public async ValueTask DisposeAsync()
    {
        _telemetry.Dispose();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task Execute_PersistsCompletedStatus_AfterProcessorClearsChangeTracker()
    {
        var ct = TestContext.Current.CancellationToken;

        var context = Substitute.For<IJobExecutionContext>();
        context.MergedJobDataMap.Returns(new JobDataMap
        {
            { StoreScrapeJob.StoreChainSlugKey, "pingodoce" },
            { StoreScrapeJob.ScrapeLocationsKey, true }
        });
        context.CancellationToken.Returns(ct);

        await _job.Execute(context, CancellationToken.None);

        var savedJob = await _db.ScrapingJobs.SingleAsync(ct);
        Assert.Equal(ScrapingJobStatus.Completed, savedJob.Status);
        Assert.NotNull(savedJob.CompletedAt);
        Assert.Equal(1, savedJob.ProductsScraped);
        Assert.Null(savedJob.ErrorMessage);

        var messages = await _db.ScrapingLogs
            .Where(l => l.ScrapingJobId == savedJob.Id)
            .Select(l => l.Message)
            .ToListAsync(ct);

        Assert.Contains(messages, m => m == "Scraped 1 store locations.");
        Assert.Contains(messages, m => m == "Completed. Scraped 1 products.");
    }

    [Fact]
    public async Task FailedCategory_JobDegraded_OutcomesPersisted()
    {
        var ct = TestContext.Current.CancellationToken;

        // A previously active product that this run will not see
        var staleId = Guid.NewGuid();
        _db.StoreProducts.Add(new StoreProduct
        {
            Id = staleId, StoreChainId = ChainId, ExternalId = "old-1", Name = "Old", IsActive = true,
            LastScraped = DateTime.UtcNow.AddDays(-30)
        });
        await _db.SaveChangesAsync(ct);

        var outcome = new ScrapeOutcome
        {
            Kind = OutcomeKind.Category, Key = "x", Result = OutcomeResult.Failed, Url = "https://e/x",
            StatusCounts = new() { [410] = 1 }
        };

        await MakeJob(new FakeStoreScraper(outcomes: [outcome])).Execute(MakeContext(ct), CancellationToken.None);

        var saved = await _db.ScrapingJobs.AsNoTracking().SingleAsync(ct);
        Assert.Equal(ScrapingJobStatus.Degraded, saved.Status);
        Assert.Contains("x", saved.HealthReason);
        Assert.True(saved.StaleMarkingSkipped);
        Assert.Equal(0, saved.WarningCount);

        var row = await _db.ScrapingRunOutcomes.AsNoTracking().SingleAsync(ct);
        Assert.Equal("410:1", row.StatusCounts);

        var errors = await _db.ScrapingLogs.AsNoTracking()
            .Where(l => l.ScrapingJobId == saved.Id && l.Level == ScrapingLogLevel.Error).ToListAsync(ct);
        Assert.Single(errors);

        Assert.True((await _db.StoreProducts.AsNoTracking().SingleAsync(p => p.Id == staleId, ct)).IsActive);
    }

    [Fact]
    public async Task CanaryFailed_SkipsScrape_DegradedNoDeactivation()
    {
        var ct = TestContext.Current.CancellationToken;
        var staleId = Guid.NewGuid();
        _db.StoreProducts.Add(new StoreProduct
        {
            Id = staleId, StoreChainId = ChainId, ExternalId = "old-1", Name = "Old", IsActive = true,
            LastScraped = DateTime.UtcNow.AddDays(-30)
        });
        await _db.SaveChangesAsync(ct);

        var scraper = new FakeStoreScraper(canary: new CanaryResult(false, "https://e/x: no product tiles", 1));
        await MakeJob(scraper).Execute(MakeContext(ct), CancellationToken.None);

        Assert.Equal(0, scraper.ScrapeCalls);
        var saved = await _db.ScrapingJobs.AsNoTracking().SingleAsync(ct);
        Assert.Equal(ScrapingJobStatus.Degraded, saved.Status);
        Assert.Equal("Canary: https://e/x: no product tiles", saved.HealthReason);
        Assert.True(saved.StaleMarkingSkipped);
        Assert.True((await _db.StoreProducts.AsNoTracking().SingleAsync(p => p.Id == staleId, ct)).IsActive);

        var row = await _db.ScrapingRunOutcomes.AsNoTracking().SingleAsync(ct);
        Assert.Equal(OutcomeKind.Canary, row.Kind);
        Assert.Equal(OutcomeResult.Failed, row.Result);
        Assert.Equal("https://e/x: no product tiles", row.FirstError);
    }

    [Fact]
    public async Task CanaryOk_RunsScrapeNormally()
    {
        var ct = TestContext.Current.CancellationToken;
        var scraper = new FakeStoreScraper();
        await MakeJob(scraper).Execute(MakeContext(ct), CancellationToken.None);

        Assert.Equal(1, scraper.ScrapeCalls);
        var saved = await _db.ScrapingJobs.AsNoTracking().SingleAsync(ct);
        Assert.Equal(ScrapingJobStatus.Completed, saved.Status);
    }

    [Fact]
    public async Task CountDrop_AdvisoryOnly_StatusCompleted_DropRatioSet()
    {
        var ct = TestContext.Current.CancellationToken;
        _db.ScrapingJobs.Add(new ScrapingJob
        {
            Id = Guid.NewGuid(), StoreChainId = ChainId, Status = ScrapingJobStatus.Completed,
            StartedAt = DateTime.UtcNow.AddDays(-1), ProductsScraped = 10
        });
        await _db.SaveChangesAsync(ct);

        await MakeJob(new FakeStoreScraper()).Execute(MakeContext(ct), CancellationToken.None);

        var saved = await _db.ScrapingJobs.AsNoTracking().OrderByDescending(j => j.StartedAt).FirstAsync(ct);
        Assert.Equal(ScrapingJobStatus.Completed, saved.Status);
        Assert.Equal(10, saved.PreviousHealthyCount);
        Assert.Equal(0.1, saved.DropRatio!.Value, 9);
        Assert.Equal(1, saved.ProductsReturned);
    }

    private sealed class FakeStoreScraper(IReadOnlyList<ScrapeOutcome>? outcomes = null, CanaryResult? canary = null) : IStoreScraper
    {
        public int ScrapeCalls { get; private set; }
        public string StoreChainSlug => "pingodoce";

        public Task<ScrapeRunResult> ScrapeProductsAsync(
            string? category = null,
            CancellationToken ct = default)
        {
            ScrapeCalls++;
            IReadOnlyList<ScrapedProduct> products =
            [
                new ScrapedProduct(
                    Name: "Leite Meio Gordo 1L",
                    Brand: "Marca Teste",
                    Category: "leite",
                    Price: 1.09m,
                    UnitPrice: 1.09m,
                    EAN: null,
                    ExternalId: "test-001",
                    ImageUrl: null,
                    SourceUrl: "https://example.test/product/1",
                    IsPromotion: false,
                    PromotionDescription: null,
                    Unit: ProductUnit.L,
                    SizeValue: 1m)
            ];

            return Task.FromResult(new ScrapeRunResult(products, outcomes ?? [], []));
        }

        public Task<CanaryResult> ProbeAsync(CancellationToken ct = default) => Task.FromResult(canary ?? new CanaryResult(true, "ok", 0));

        public Task<IReadOnlyList<ScrapedStoreLocation>> ScrapeStoreLocationsAsync(CancellationToken ct = default)
        {
            IReadOnlyList<ScrapedStoreLocation> locations =
            [
                new ScrapedStoreLocation(
                    Name: "Pingo Doce Teste",
                    Address: "Rua Teste",
                    PostalCode: "1000-000",
                    City: "Lisboa",
                    Latitude: 38.7223,
                    Longitude: -9.1393)
            ];

            return Task.FromResult(locations);
        }
    }
}
