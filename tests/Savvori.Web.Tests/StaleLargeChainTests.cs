using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Savvori.Shared;
using Savvori.WebApi;
using Savvori.WebApi.Scraping;

namespace Savvori.Web.Tests;

/// <summary>
/// Regression: the stale query used to translate "not in scraped ids" into one SQL parameter per id,
/// failing on SQLite ("too many SQL variables") for chains with tens of thousands of products.
/// </summary>
public class StaleLargeChainTests : IAsyncLifetime
{
    private SqliteConnection _conn = default!;
    private SavvoriDbContext _db = default!;
    private Guid _chainId;

    public async ValueTask InitializeAsync()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        await _conn.OpenAsync(TestContext.Current.CancellationToken);
        _db = new SavvoriDbContext(new DbContextOptionsBuilder<SavvoriDbContext>().UseSqlite(_conn).Options);
        await _db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        _chainId = Guid.NewGuid();
        _db.StoreChains.Add(new StoreChain
        {
            Id = _chainId, Name = "auchan", Slug = "auchan", BaseUrl = "https://auchan.pt", IsActive = true
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }

    [Fact]
    public async Task LargeScrape_DoesNotHitSqliteVariableLimit_AndStillMarksUnseenStale()
    {
        var ct = TestContext.Current.CancellationToken;
        const int seen = 40_000;
        var old = DateTime.UtcNow.AddDays(-10);

        // Seed active products directly: the seen ids plus one that the scrape will not mention.
        var seeded = Enumerable.Range(0, seen).Select(i => $"seen-{i}").Append("unseen")
            .Select(ext => new StoreProduct
            {
                Id = Guid.NewGuid(), StoreChainId = _chainId, ExternalId = ext, Name = ext,
                IsActive = true, LastScraped = old
            }).ToList();
        _db.StoreProducts.AddRange(seeded);
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();

        // Scraped items with a null name fail in ProcessOneProductAsync and are skipped (cheap),
        // but their ids still count as "seen" for the stale query.
        var scraped = Enumerable.Range(0, seen).Select(i => new ScrapedProduct(
            Name: null!, Brand: null, Category: null, Price: 1m, UnitPrice: null, EAN: null,
            ExternalId: $"seen-{i}", ImageUrl: null, SourceUrl: "https://example.com",
            IsPromotion: false, PromotionDescription: null, Unit: ProductUnit.L, SizeValue: null)).ToList();

        var processor = new ScraperResultProcessor(_db, NullLogger<ScraperResultProcessor>.Instance);
        var policy = new StalePolicy(true, TimeSpan.FromDays(3), [DateTime.UtcNow.AddDays(-5), DateTime.UtcNow]);
        var result = await processor.ProcessProductsAsync("auchan", scraped, policy, ct);

        Assert.Equal(1, result.StaleMarked);
        _db.ChangeTracker.Clear();
        Assert.False((await _db.StoreProducts.SingleAsync(s => s.ExternalId == "unseen", ct)).IsActive);
        Assert.Equal(seen, await _db.StoreProducts.CountAsync(s => s.IsActive, ct));
    }
}
