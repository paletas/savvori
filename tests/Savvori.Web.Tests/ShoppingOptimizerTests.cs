using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Savvori.Shared;
using Savvori.WebApi;
using Savvori.WebApi.Services;

namespace Savvori.Web.Tests;

/// <summary>
/// Unit tests for ShoppingOptimizer using EF Core InMemory provider.
/// </summary>
public class ShoppingOptimizerTests : IDisposable
{
    private readonly SavvoriDbContext _db;
    private readonly ILocationService _locationService;
    private readonly ShoppingOptimizer _optimizer;

    // Shared test IDs
    private static readonly Guid ListId = Guid.NewGuid();
    private static readonly Guid Product1Id = Guid.NewGuid();
    private static readonly Guid Product2Id = Guid.NewGuid();
    private static readonly Guid Item1Id = Guid.NewGuid();
    private static readonly Guid Item2Id = Guid.NewGuid();
    private static readonly Guid Store1Id = Guid.NewGuid(); // Continente
    private static readonly Guid Store2Id = Guid.NewGuid(); // Pingo Doce
    private static readonly Guid Chain1Id = Guid.NewGuid();
    private static readonly Guid Chain2Id = Guid.NewGuid();

    public ShoppingOptimizerTests()
    {
        var options = new DbContextOptionsBuilder<SavvoriDbContext>()
            .UseInMemoryDatabase(databaseName: $"OptimizerTests_{Guid.NewGuid()}")
            .Options;
        _db = new SavvoriDbContext(options);

        _locationService = Substitute.For<ILocationService>();
        _locationService.CalculateDistanceKm(
            Arg.Any<double>(), Arg.Any<double>(),
            Arg.Any<double>(), Arg.Any<double>())
            .Returns(5.0); // all stores 5km away

        var logger = Substitute.For<ILogger<ShoppingOptimizer>>();
        _optimizer = new ShoppingOptimizer(_db, _locationService, logger);

        SeedData();
    }

    private void SeedData()
    {
        var chain1 = new StoreChain { Id = Chain1Id, Name = "Continente", Slug = "continente", BaseUrl = "https://continente.pt", IsActive = true };
        var chain2 = new StoreChain { Id = Chain2Id, Name = "Pingo Doce", Slug = "pingodoce", BaseUrl = "https://pingodoce.pt", IsActive = true };

        var store1 = new Store { Id = Store1Id, Name = "Continente Lisboa", StoreChainId = Chain1Id, Latitude = 38.72, Longitude = -9.14, IsActive = true };
        var store2 = new Store { Id = Store2Id, Name = "Pingo Doce Lisboa", StoreChainId = Chain2Id, Latitude = 38.71, Longitude = -9.13, IsActive = true };

        var product1 = new Product { Id = Product1Id, Name = "Leite Mimosa 1L", Brand = "Mimosa", Unit = ProductUnit.L, SizeValue = 1m };
        var product2 = new Product { Id = Product2Id, Name = "Iogurte Yoplait 500g", Brand = "Yoplait", Unit = ProductUnit.G, SizeValue = 500m };

        _db.StoreChains.AddRange(chain1, chain2);
        _db.Stores.AddRange(store1, store2);
        _db.Products.AddRange(product1, product2);

        // StoreProducts: one per chain per product
        var sp1Chain1 = new StoreProduct { Id = Guid.NewGuid(), StoreChainId = Chain1Id, ExternalId = "leite-c", Name = "Leite Mimosa 1L", Unit = ProductUnit.L, SizeValue = 1m, CanonicalProductId = Product1Id, IsActive = true, MatchStatus = MatchStatus.AutoMatched, FirstSeen = DateTime.UtcNow, LastScraped = DateTime.UtcNow };
        var sp1Chain2 = new StoreProduct { Id = Guid.NewGuid(), StoreChainId = Chain2Id, ExternalId = "leite-p", Name = "Leite Mimosa 1L", Unit = ProductUnit.L, SizeValue = 1m, CanonicalProductId = Product1Id, IsActive = true, MatchStatus = MatchStatus.AutoMatched, FirstSeen = DateTime.UtcNow, LastScraped = DateTime.UtcNow };
        var sp2Chain1 = new StoreProduct { Id = Guid.NewGuid(), StoreChainId = Chain1Id, ExternalId = "iog-c", Name = "Iogurte Yoplait 500g", Unit = ProductUnit.G, SizeValue = 500m, CanonicalProductId = Product2Id, IsActive = true, MatchStatus = MatchStatus.AutoMatched, FirstSeen = DateTime.UtcNow, LastScraped = DateTime.UtcNow };
        var sp2Chain2 = new StoreProduct { Id = Guid.NewGuid(), StoreChainId = Chain2Id, ExternalId = "iog-p", Name = "Iogurte Yoplait 500g", Unit = ProductUnit.G, SizeValue = 500m, CanonicalProductId = Product2Id, IsActive = true, MatchStatus = MatchStatus.AutoMatched, FirstSeen = DateTime.UtcNow, LastScraped = DateTime.UtcNow };

        _db.StoreProducts.AddRange(sp1Chain1, sp1Chain2, sp2Chain1, sp2Chain2);

        _db.StoreProductPrices.AddRange(
            new StoreProductPrice { Id = Guid.NewGuid(), StoreProductId = sp1Chain1.Id, Price = 1.09m, IsLatest = true, IsPromotion = false, ScrapedAt = DateTime.UtcNow },
            new StoreProductPrice { Id = Guid.NewGuid(), StoreProductId = sp1Chain2.Id, Price = 0.89m, IsLatest = true, IsPromotion = true, ScrapedAt = DateTime.UtcNow },
            new StoreProductPrice { Id = Guid.NewGuid(), StoreProductId = sp2Chain1.Id, Price = 2.49m, IsLatest = true, IsPromotion = false, ScrapedAt = DateTime.UtcNow },
            new StoreProductPrice { Id = Guid.NewGuid(), StoreProductId = sp2Chain2.Id, Price = 2.79m, IsLatest = true, IsPromotion = false, ScrapedAt = DateTime.UtcNow }
        );

        _db.ShoppingListItems.AddRange(
            new ShoppingListItem { Id = Item1Id, ShoppingListId = ListId, ProductId = Product1Id, Quantity = 2 },
            new ShoppingListItem { Id = Item2Id, ShoppingListId = ListId, ProductId = Product2Id, Quantity = 1 }
        );

        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    // ─── OptimizeCheapestTotalAsync ────────────────────────────────────────────

    [Fact]
    public async Task OptimizeCheapestTotal_ReturnsCorrectMode()
    {
        var result = await _optimizer.OptimizeCheapestTotalAsync(ListId, new OptimizationContext(), CancellationToken.None);
        Assert.Equal("cheapest-total", result.OptimizationMode);
    }

    [Fact]
    public async Task OptimizeCheapestTotal_ReturnsAllItems()
    {
        var result = await _optimizer.OptimizeCheapestTotalAsync(ListId, new OptimizationContext(), CancellationToken.None);
        Assert.Equal(2, result.Items.Count);
    }

    [Fact]
    public async Task OptimizeCheapestTotal_AssignsCheapestStorePerItem()
    {
        var result = await _optimizer.OptimizeCheapestTotalAsync(ListId, new OptimizationContext(), CancellationToken.None);

        // Leite is cheaper at Pingo Doce (0.89)
        var leiteItem = result.Items.First(i => i.ShoppingListItemId == Item1Id);
        Assert.Equal(Chain2Id, leiteItem.StoreId);
        Assert.Equal(0.89m, leiteItem.UnitPrice);

        // Iogurte is cheaper at Continente (2.49)
        var iogurteItem = result.Items.First(i => i.ShoppingListItemId == Item2Id);
        Assert.Equal(Chain1Id, iogurteItem.StoreId);
        Assert.Equal(2.49m, iogurteItem.UnitPrice);
    }

    [Fact]
    public async Task OptimizeCheapestTotal_CalculatesTotalCostCorrectly()
    {
        var result = await _optimizer.OptimizeCheapestTotalAsync(ListId, new OptimizationContext(), CancellationToken.None);

        // Leite: 0.89 * 2 = 1.78 + Iogurte: 2.49 * 1 = 2.49 → total = 4.27
        Assert.Equal(4.27m, result.TotalCost);
    }

    [Fact]
    public async Task OptimizeCheapestTotal_ReportsPromotionCorrectly()
    {
        var result = await _optimizer.OptimizeCheapestTotalAsync(ListId, new OptimizationContext(), CancellationToken.None);

        var leiteItem = result.Items.First(i => i.ShoppingListItemId == Item1Id);
        Assert.True(leiteItem.IsPromotion);
    }

    [Fact]
    public async Task OptimizeCheapestTotal_QuantityMultipliedInTotalPrice()
    {
        var result = await _optimizer.OptimizeCheapestTotalAsync(ListId, new OptimizationContext(), CancellationToken.None);

        var leiteItem = result.Items.First(i => i.ShoppingListItemId == Item1Id);
        Assert.Equal(2, leiteItem.Quantity);
        Assert.Equal(1.78m, leiteItem.TotalPrice);
    }

    [Fact]
    public async Task OptimizeCheapestTotal_EmptyList_ReturnsEmptyResult()
    {
        var emptyListId = Guid.NewGuid();
        var result = await _optimizer.OptimizeCheapestTotalAsync(emptyListId, new OptimizationContext(), CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Equal(0m, result.TotalCost);
    }

    // ─── OptimizeCheapestStoreAsync ────────────────────────────────────────────

    [Fact]
    public async Task OptimizeCheapestStore_ReturnsCorrectMode()
    {
        var result = await _optimizer.OptimizeCheapestStoreAsync(ListId, new OptimizationContext(), CancellationToken.None);
        Assert.Equal("cheapest-store", result.OptimizationMode);
    }

    [Fact]
    public async Task OptimizeCheapestStore_AllItemsFromSingleStore()
    {
        var result = await _optimizer.OptimizeCheapestStoreAsync(ListId, new OptimizationContext(), CancellationToken.None);

        // All items should be from the same store
        var storeIds = result.Items.Select(i => i.StoreId).Distinct().ToList();
        Assert.Single(storeIds);
    }

    [Fact]
    public async Task OptimizeCheapestStore_SelectsStoreThatMinimizesTotal()
    {
        // Continente total: (1.09 * 2) + 2.49 = 4.67
        // Pingo Doce total: (0.89 * 2) + 2.79 = 4.57
        // Pingo Doce should win
        var result = await _optimizer.OptimizeCheapestStoreAsync(ListId, new OptimizationContext(), CancellationToken.None);

        Assert.All(result.Items, item => Assert.Equal(Chain2Id, item.StoreId));
    }

    // ─── CompareAllStoresAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task CompareAllStores_ReturnsBothStores()
    {
        var matrix = await _optimizer.CompareAllStoresAsync(ListId, new OptimizationContext(), CancellationToken.None);
        Assert.Equal(2, matrix.Stores.Count);
    }

    [Fact]
    public async Task CompareAllStores_ReturnsRowPerItem()
    {
        var matrix = await _optimizer.CompareAllStoresAsync(ListId, new OptimizationContext(), CancellationToken.None);
        Assert.Equal(2, matrix.Rows.Count);
    }

    [Fact]
    public async Task CompareAllStores_HasPriceForEveryStoreInEveryRow()
    {
        var matrix = await _optimizer.CompareAllStoresAsync(ListId, new OptimizationContext(), CancellationToken.None);

        foreach (var row in matrix.Rows)
        {
            Assert.Equal(2, row.PricesByStore.Count);
            Assert.All(row.PricesByStore.Values, price => Assert.NotNull(price));
        }
    }

    [Fact]
    public async Task CompareAllStores_IdentifiesCheapestPricePerRow()
    {
        var matrix = await _optimizer.CompareAllStoresAsync(ListId, new OptimizationContext(), CancellationToken.None);

        var leiteRow = matrix.Rows.First(r => r.ShoppingListItemId == Item1Id);
        Assert.Equal(0.89m, leiteRow.CheapestPrice);
        Assert.Equal(Chain2Id, leiteRow.CheapestStoreId);
    }

    [Fact]
    public async Task CompareAllStores_StoreTotalsAreCorrect()
    {
        var matrix = await _optimizer.CompareAllStoresAsync(ListId, new OptimizationContext(), CancellationToken.None);

        // Continente: (1.09 * 2) + (2.49 * 1) = 4.67
        var continente = matrix.Stores.First(s => s.StoreId == Chain1Id);
        Assert.Equal(4.67m, continente.Total);

        // Pingo Doce: (0.89 * 2) + (2.79 * 1) = 4.57
        var pingodoce = matrix.Stores.First(s => s.StoreId == Chain2Id);
        Assert.Equal(4.57m, pingodoce.Total);
    }

    // ─── Missing items ─────────────────────────────────────────────────────────

    [Fact]
    public async Task OptimizeCheapestTotal_ItemWithNoPrice_AppearInMissingItems()
    {
        var orphanProductId = Guid.NewGuid();
        var orphanItemId = Guid.NewGuid();
        var orphanListId = Guid.NewGuid();

        _db.Products.Add(new Product { Id = orphanProductId, Name = "Produto Raro" });
        _db.ShoppingListItems.Add(new ShoppingListItem { Id = orphanItemId, ShoppingListId = orphanListId, ProductId = orphanProductId, Quantity = 1 });
        await _db.SaveChangesAsync();

        var result = await _optimizer.OptimizeCheapestTotalAsync(orphanListId, new OptimizationContext(), CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Single(result.MissingItems);
        Assert.Equal(orphanItemId, result.MissingItems[0].ShoppingListItemId);
    }

    // ─── Store filtering by context.StoreIds ──────────────────────────────────

    [Fact]
    public async Task OptimizeCheapestTotal_FiltersToRequestedStoreIds()
    {
        // Only allow Continente (Chain1)
        var context = new OptimizationContext { StoreIds = [Chain1Id] };
        var result = await _optimizer.OptimizeCheapestTotalAsync(ListId, context, CancellationToken.None);

        Assert.All(result.Items, item => Assert.Equal(Chain1Id, item.StoreId));
    }
}
