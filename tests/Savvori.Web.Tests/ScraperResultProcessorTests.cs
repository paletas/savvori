using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Savvori.Shared;
using Savvori.WebApi;
using Savvori.WebApi.Scraping;

namespace Savvori.Web.Tests;

public class ScraperResultProcessorTests : IAsyncLifetime
{
    private SavvoriDbContext _db = default!;
    private ScraperResultProcessor _processor = default!;

    private static readonly Guid ChainId = Guid.NewGuid();

    public async ValueTask InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<SavvoriDbContext>()
            .UseInMemoryDatabase($"ProcessorTests_{Guid.NewGuid()}")
            .Options;
        _db = new SavvoriDbContext(options);

        _db.StoreChains.Add(new StoreChain
        {
            Id = ChainId,
            Name = "Continente",
            Slug = "continente",
            BaseUrl = "https://continente.pt",
            IsActive = true
        });
        await _db.SaveChangesAsync();

        // Seed categories so CategoryId can be resolved
        await CategorySeeder.SeedAsync(_db);

        _processor = new ScraperResultProcessor(_db, NullLogger<ScraperResultProcessor>.Instance);
    }

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();

    private static ScrapedProduct MakeScraped(
        string name = "Leite Mimosa 1L",
        string? category = "leite",
        string? ean = null,
        string? externalId = null,
        decimal price = 0.99m) =>
        new(
            Name: name,
            Brand: "Mimosa",
            Category: category,
            Price: price,
            UnitPrice: null,
            EAN: ean,
            ExternalId: externalId ?? Guid.NewGuid().ToString(),
            ImageUrl: null,
            SourceUrl: "https://example.com",
            IsPromotion: false,
            PromotionDescription: null,
            Unit: ProductUnit.L,
            SizeValue: 1m);

    // ─── StoreProduct creation ─────────────────────────────────────────────────

    [Fact]
    public async Task ProcessProductsAsync_CreatesStoreProduct_WhenNotExists()
    {
        var scraped = MakeScraped();
        await _processor.ProcessProductsAsync("continente", [scraped]);

        var storeProducts = await _db.StoreProducts.ToListAsync();
        Assert.Single(storeProducts);
        Assert.Equal(scraped.ExternalId, storeProducts[0].ExternalId);
        Assert.Equal(ChainId, storeProducts[0].StoreChainId);
    }

    [Fact]
    public async Task ProcessProductsAsync_DoesNotDuplicateStoreProduct_OnReScrape()
    {
        var externalId = "ext-001";
        await _processor.ProcessProductsAsync("continente", [MakeScraped(externalId: externalId)]);
        await _processor.ProcessProductsAsync("continente", [MakeScraped(externalId: externalId, price: 1.50m)]);

        var count = await _db.StoreProducts.CountAsync();
        Assert.Equal(1, count);
    }

    // ─── Canonical product matching ────────────────────────────────────────────

    [Fact]
    public async Task ProcessProductsAsync_CreatesCanonicalProduct_WhenNoMatch()
    {
        var scraped = MakeScraped();
        await _processor.ProcessProductsAsync("continente", [scraped]);

        var products = await _db.Products.ToListAsync();
        Assert.Single(products);
        Assert.Equal(scraped.Name, products[0].Name);
    }

    [Fact]
    public async Task ProcessProductsAsync_MatchesExistingProduct_ByNormalizedNameAndSize()
    {
        // First insertion creates the canonical product
        await _processor.ProcessProductsAsync("continente", [MakeScraped()]);

        // Second run with different externalId but same product data
        await _processor.ProcessProductsAsync("continente", [MakeScraped(externalId: "ext-2")]);

        var count = await _db.Products.CountAsync();
        Assert.Equal(1, count); // no duplicate canonical product
    }

    [Fact]
    public async Task ProcessProductsAsync_MatchesExistingProduct_ByEAN()
    {
        // Seed a canonical product with an EAN
        var productId = Guid.NewGuid();
        _db.Products.Add(new Product
        {
            Id = productId,
            Name = "Leite UHT",
            NormalizedName = ProductNormalizer.Normalize("Leite UHT"),
            EAN = "5601234567890",
            Unit = ProductUnit.L,
            SizeValue = 1m
        });
        await _db.SaveChangesAsync();

        // Process a scraped product with the same EAN
        await _processor.ProcessProductsAsync("continente", [MakeScraped(ean: "5601234567890", externalId: "ean-match-ext")]);

        var sp = await _db.StoreProducts.FirstAsync();
        Assert.Equal(productId, sp.CanonicalProductId);
        Assert.Equal("ean", sp.MatchMethod);
    }

    [Fact]
    public async Task ProcessProductsAsync_SetsMatchStatus_AutoMatched_OnCreation()
    {
        await _processor.ProcessProductsAsync("continente", [MakeScraped()]);

        var sp = await _db.StoreProducts.FirstAsync();
        Assert.Equal(MatchStatus.AutoMatched, sp.MatchStatus);
        Assert.Equal("created-new", sp.MatchMethod);
    }

    // ─── Category assignment ───────────────────────────────────────────────────

    [Fact]
    public async Task ProcessProductsAsync_AssignsCategoryId_WhenCategoryMapped()
    {
        await _processor.ProcessProductsAsync("continente", [MakeScraped(category: "leite")]);

        var product = await _db.Products.FirstOrDefaultAsync();
        Assert.NotNull(product);
        Assert.NotNull(product.CategoryId);

        var category = await _db.ProductCategories.FindAsync(product.CategoryId);
        Assert.NotNull(category);
        Assert.Equal("leite", category.Slug);
    }

    [Fact]
    public async Task ProcessProductsAsync_BackfillsCategoryId_WhenExistingProductHasNone()
    {
        var productId = Guid.NewGuid();
        _db.Products.Add(new Product
        {
            Id = productId,
            Name = "Leite Mimosa 1L",
            NormalizedName = ProductNormalizer.Normalize("Leite Mimosa 1L"),
            Brand = "Mimosa",
            Unit = ProductUnit.L,
            SizeValue = 1m,
            CategoryId = null
        });
        await _db.SaveChangesAsync();

        await _processor.ProcessProductsAsync("continente", [MakeScraped(category: "leite")]);

        var product = await _db.Products.FindAsync(productId);
        Assert.NotNull(product);
        Assert.NotNull(product.CategoryId);
    }

    // ─── Price history ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessProductsAsync_CreatesStoreProductPrice_AsLatest()
    {
        await _processor.ProcessProductsAsync("continente", [MakeScraped(price: 1.09m)]);

        var prices = await _db.StoreProductPrices.ToListAsync();
        Assert.Single(prices);
        Assert.True(prices[0].IsLatest);
        Assert.Equal(1.09m, prices[0].Price);
    }

    [Fact]
    public async Task ProcessProductsAsync_MarksOldPriceHistorical_OnReScrape()
    {
        var externalId = "ext-rescrape";
        await _processor.ProcessProductsAsync("continente", [MakeScraped(externalId: externalId, price: 1.09m)]);
        await _processor.ProcessProductsAsync("continente", [MakeScraped(externalId: externalId, price: 0.89m)]);

        var prices = await _db.StoreProductPrices.ToListAsync();
        Assert.Equal(2, prices.Count);
        Assert.Single(prices, p => p.IsLatest);
        Assert.Equal(0.89m, prices.Single(p => p.IsLatest).Price);
    }

    // ─── Inactive marking ─────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessProductsAsync_MarksStaleProducts_AsInactive()
    {
        var ext1 = "ext-keep";
        var ext2 = "ext-gone";
        await _processor.ProcessProductsAsync("continente", [
            MakeScraped(externalId: ext1),
            MakeScraped(externalId: ext2)
        ]);

        // Second scrape omits ext2
        await _processor.ProcessProductsAsync("continente", [MakeScraped(externalId: ext1)]);

        var sp2 = await _db.StoreProducts.FirstAsync(sp => sp.ExternalId == ext2);
        Assert.False(sp2.IsActive);
    }

    // ─── EAN re-match (self-healing) ──────────────────────────────────────────

    [Fact]
    public async Task ProcessProductsAsync_ReMatchesByEAN_WhenEANAdded()
    {
        // First scrape: no EAN, creates canonical product A via name+size
        await _processor.ProcessProductsAsync("continente", [MakeScraped(ean: null, externalId: "ext-ean-upgrade")]);
        var sp = await _db.StoreProducts.FirstAsync(s => s.ExternalId == "ext-ean-upgrade");
        var originalCanonicalId = sp.CanonicalProductId;

        // Seed a different canonical product with the EAN we're about to see
        var targetProduct = new Product
        {
            Id = Guid.NewGuid(),
            Name = "Leite Mimosa 1L",
            EAN = "5601234000001",
            NormalizedName = ProductNormalizer.Normalize("Leite Mimosa 1L"),
            Unit = ProductUnit.L,
            SizeValue = 1m
        };
        _db.Products.Add(targetProduct);
        await _db.SaveChangesAsync();

        // Second scrape: same externalId, but now has EAN
        await _processor.ProcessProductsAsync("continente", [MakeScraped(ean: "5601234000001", externalId: "ext-ean-upgrade")]);

        sp = await _db.StoreProducts.FirstAsync(s => s.ExternalId == "ext-ean-upgrade");
        Assert.Equal(targetProduct.Id, sp.CanonicalProductId);
        Assert.Equal("ean", sp.MatchMethod);
    }

    // ─── Unknown chain ────────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessProductsAsync_Returns0_WhenChainNotFound()
    {
        var count = await _processor.ProcessProductsAsync("nonexistent-chain", [MakeScraped()]);
        Assert.Equal(0, count);
    }
}