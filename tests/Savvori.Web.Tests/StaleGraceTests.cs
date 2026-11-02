using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Savvori.Shared;
using Savvori.WebApi;
using Savvori.WebApi.Scraping;

namespace Savvori.Web.Tests;

public class StaleGraceTests : IAsyncLifetime
{
    private SavvoriDbContext _db = default!;
    private ScraperResultProcessor _processor = default!;

    public async ValueTask InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<SavvoriDbContext>()
            .UseInMemoryDatabase($"StaleGraceTests_{Guid.NewGuid()}")
            .Options;
        _db = new SavvoriDbContext(options);
        foreach (var slug in new[] { "continente", "lidl" })
            _db.StoreChains.Add(new StoreChain
            {
                Id = Guid.NewGuid(), Name = slug, Slug = slug, BaseUrl = $"https://{slug}.pt", IsActive = true
            });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await CategorySeeder.SeedAsync(_db, ct: TestContext.Current.CancellationToken);
        _processor = new ScraperResultProcessor(_db, NullLogger<ScraperResultProcessor>.Instance);
    }

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();

    private static ScrapedProduct MakeScraped(string externalId) =>
        new(
            Name: "Leite Mimosa 1L",
            Brand: "Mimosa",
            Category: "leite",
            Price: 0.99m,
            UnitPrice: null,
            EAN: null,
            ExternalId: externalId,
            ImageUrl: null,
            SourceUrl: "https://example.com",
            IsPromotion: false,
            PromotionDescription: null,
            Unit: ProductUnit.L,
            SizeValue: 1m);

    /// <summary>Scrapes one product, then sets its LastScraped to <paramref name="age"/> ago.</summary>
    private async Task SeedAsync(string chain, string ext, TimeSpan age, bool active = true)
    {
        var ct = TestContext.Current.CancellationToken;
        await _processor.ProcessProductsAsync(chain, [MakeScraped(ext)], ct: ct);
        var sp = await _db.StoreProducts.SingleAsync(s => s.ExternalId == ext, ct);
        sp.LastScraped = DateTime.UtcNow - age;
        sp.IsActive = active;
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
    }

    private async Task<bool> IsActiveAsync(string ext) =>
        (await _db.StoreProducts.AsNoTracking().SingleAsync(s => s.ExternalId == ext, TestContext.Current.CancellationToken)).IsActive;

    private Task<ProcessResult> RunAsync(string chain, StalePolicy policy) =>
        _processor.ProcessProductsAsync(chain, [MakeScraped("other")], policy, TestContext.Current.CancellationToken);

    private static DateTime Now => DateTime.UtcNow;

    [Fact]
    public async Task DegradedRun_DeactivatesNothing()
    {
        await SeedAsync("continente", "a", TimeSpan.FromDays(10));
        var r = await RunAsync("continente", new StalePolicy(false, TimeSpan.FromDays(3), [Now.AddDays(-9), Now]));
        Assert.True(await IsActiveAsync("a"));
        Assert.Equal(0, r.StaleMarked);
    }

    [Fact]
    public async Task HealthyRun_WithinGrace_KeepsActive()
    {
        await SeedAsync("continente", "a", TimeSpan.FromDays(1));
        await RunAsync("continente", new StalePolicy(true, TimeSpan.FromDays(3), [Now.AddHours(-12), Now]));
        Assert.True(await IsActiveAsync("a"));
    }

    [Fact]
    public async Task HealthyRun_PastGrace_Deactivates()
    {
        await SeedAsync("continente", "a", TimeSpan.FromDays(5));
        var r = await RunAsync("continente", new StalePolicy(true, TimeSpan.FromDays(3), [Now.AddDays(-4), Now]));
        Assert.False(await IsActiveAsync("a"));
        Assert.Equal(1, r.StaleMarked);
    }

    [Fact]
    public async Task Lidl_14DayGrace()
    {
        await SeedAsync("lidl", "a", TimeSpan.FromDays(5));
        await RunAsync("lidl", new StalePolicy(true, TimeSpan.FromDays(14), [Now.AddDays(-4), Now]));
        Assert.True(await IsActiveAsync("a"));

        await SeedAsync("lidl", "b", TimeSpan.FromDays(20));
        await RunAsync("lidl", new StalePolicy(true, TimeSpan.FromDays(14), [Now.AddDays(-15), Now]));
        Assert.False(await IsActiveAsync("b"));
    }

    [Fact]
    public async Task UnhealthyRunsDoNotStartGrace()
    {
        await SeedAsync("continente", "a", TimeSpan.FromDays(10));
        await RunAsync("continente", new StalePolicy(true, TimeSpan.FromDays(3), [Now]));
        Assert.True(await IsActiveAsync("a"));
    }

    [Fact]
    public async Task SeenAgain_Reactivates()
    {
        await SeedAsync("continente", "a", TimeSpan.FromDays(10), active: false);
        Assert.False(await IsActiveAsync("a"));
        await _processor.ProcessProductsAsync("continente", [MakeScraped("a")], ct: TestContext.Current.CancellationToken);
        _db.ChangeTracker.Clear();
        Assert.True(await IsActiveAsync("a"));
    }
}
