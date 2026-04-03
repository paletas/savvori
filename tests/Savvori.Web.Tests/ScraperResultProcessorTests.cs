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
    private static readonly Guid StoreId = Guid.NewGuid();

    public async ValueTask InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<SavvoriDbContext>()
            .UseInMemoryDatabase($"ProcessorTests_{Guid.NewGuid()}")
            .Options;
        _db = new SavvoriDbContext(options);

        // Seed chain and store
        _db.StoreChains.Add(new StoreChain
        {
            Id = ChainId,
            Name = "Continente",
            Slug = "continente",
            BaseUrl = "https://continente.pt",
            IsActive = true
        });
        _db.Stores.Add(new Store
        {
            Id = StoreId,
            Name = "Continente Lisboa",
            StoreChainId = ChainId,
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

    // ─── Creation / matching ───────────────────────────────────────────────────

    [Fact]
    public async Task ProcessProductsAsync_CreatesNewProduct_WhenNotExists()
    {
        var scraped = MakeScraped();
        await _processor.ProcessProductsAsync("continente", [scraped]);

        var products = await _db.Products.ToListAsync();
        Assert.Single(products);
        Assert.Equal(scraped.Name, products[0].Name);
    }

    [Fact]
    public async Task ProcessProductsAsync_MatchesExistingProduct_ByNormalizedName()
    {
        // First insertion creates the product
        var scraped = MakeScraped();
        await _processor.ProcessProductsAsync("continente", [scraped]);

        // Second run with same product — should NOT create a duplicate
        var scraped2 = MakeScraped(externalId: "ext-2");
        await _processor.ProcessProductsAsync("continente", [scraped2]);

        var count = await _db.Products.CountAsync();
        Assert.Equal(1, count);
    }

    // ─── Category assignment ───────────────────────────────────────────────────

    [Fact]
    public async Task ProcessProductsAsync_AssignsCategoryId_WhenCategoryMapped()
    {
        var scraped = MakeScraped(category: "leite");
        await _processor.ProcessProductsAsync("continente", [scraped]);

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
        // Create product without CategoryId
        var productId = Guid.NewGuid();
        _db.Products.Add(new Product
        {
            Id = productId,
            Name = "Leite Mimosa 1L",
            NormalizedName = ProductNormalizer.Normalize("Leite Mimosa 1L"),
            Unit = ProductUnit.L,
            SizeValue = 1m,
            CategoryId = null
        });
        await _db.SaveChangesAsync();

        // Process scraped with category info
        var scraped = MakeScraped(category: "leite");
        await _processor.ProcessProductsAsync("continente", [scraped]);

        var product = await _db.Products.FindAsync(productId);
        Assert.NotNull(product);
        Assert.NotNull(product.CategoryId);
    }

    // ─── Price management ─────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessProductsAsync_CreatesNewPrice_AsLatest()
    {
        var scraped = MakeScraped();
        await _processor.ProcessProductsAsync("continente", [scraped]);

        var prices = await _db.ProductPrices.ToListAsync();
        Assert.Single(prices);
        Assert.True(prices[0].IsLatest);
        Assert.Equal(scraped.Price, prices[0].Price);
    }

    [Fact]
    public async Task ProcessProductsAsync_MarksOldPricesAsHistorical()
    {
        var scraped = MakeScraped(price: 1.09m);
        await _processor.ProcessProductsAsync("continente", [scraped]);

        // Second scrape with new price
        var scraped2 = MakeScraped(price: 0.89m, externalId: scraped.ExternalId);
        await _processor.ProcessProductsAsync("continente", [scraped2]);

        var prices = await _db.ProductPrices.ToListAsync();
        Assert.Equal(2, prices.Count);
        Assert.Single(prices, p => p.IsLatest);
        Assert.Equal(0.89m, prices.Single(p => p.IsLatest).Price);
    }

    // ─── Store link ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessProductsAsync_UpsertStoreLink_CreatesNew()
    {
        var scraped = MakeScraped();
        await _processor.ProcessProductsAsync("continente", [scraped]);

        var links = await _db.ProductStoreLinks.ToListAsync();
        Assert.Single(links);
        Assert.Equal(scraped.ExternalId, links[0].ExternalId);
    }

    [Fact]
    public async Task ProcessProductsAsync_UpsertStoreLink_UpdatesExisting()
    {
        var externalId = "ext-001";
        var scraped = MakeScraped(externalId: externalId);
        await _processor.ProcessProductsAsync("continente", [scraped]);

        // Second scrape — should not create a second link
        var scraped2 = MakeScraped(externalId: externalId, price: 1.20m);
        await _processor.ProcessProductsAsync("continente", [scraped2]);

        var links = await _db.ProductStoreLinks.ToListAsync();
        Assert.Single(links);
    }
}
