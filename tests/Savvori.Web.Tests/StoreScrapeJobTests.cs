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

        var processor = new ScraperResultProcessor(_db, NullLogger<ScraperResultProcessor>.Instance);
        _telemetry = new ScrapingTelemetry();

        _job = new StoreScrapeJob(
            [new FakeStoreScraper()],
            processor,
            _db,
            _telemetry,
            NullLogger<StoreScrapeJob>.Instance);
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

        await _job.Execute(context);

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

    private sealed class FakeStoreScraper : IStoreScraper
    {
        public string StoreChainSlug => "pingodoce";

        public Task<IReadOnlyList<ScrapedProduct>> ScrapeProductsAsync(
            string? category = null,
            CancellationToken ct = default)
        {
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

            return Task.FromResult(products);
        }

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
