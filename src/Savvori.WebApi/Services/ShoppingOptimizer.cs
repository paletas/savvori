using Microsoft.EntityFrameworkCore;
using Savvori.Shared;

namespace Savvori.WebApi.Services;

/// <summary>
/// Implements price optimization across stores using data from the product-price database.
/// </summary>
public sealed class ShoppingOptimizer : IShoppingOptimizer
{
    private readonly SavvoriDbContext _db;
    private readonly ILocationService _locationService;
    private readonly ILogger<ShoppingOptimizer> _logger;

    public ShoppingOptimizer(
        SavvoriDbContext db,
        ILocationService locationService,
        ILogger<ShoppingOptimizer> logger)
    {
        _db = db;
        _locationService = locationService;
        _logger = logger;
    }

    // ─── Public API ────────────────────────────────────────────────────────────

    public async Task<OptimizationResult> OptimizeCheapestTotalAsync(
        Guid shoppingListId, OptimizationContext context, CancellationToken ct = default)
    {
        var (items, priceMap, storeDistances, missing, altsByCategory) = await LoadData(shoppingListId, context, ct);

        var optimizedItems = new List<OptimizedItem>();
        var storeTotals = new Dictionary<Guid, (string Name, string Slug, decimal Total, int Count)>();

        foreach (var item in items)
        {
            if (!priceMap.TryGetValue(item.ProductId, out var storePrices) || storePrices.Count == 0)
            {
                missing.Add(new MissingItem { ShoppingListItemId = item.Id, ProductName = item.Product?.Name ?? "Unknown" });
                continue;
            }

            var cheapest = storePrices.MinBy(sp => sp.Price)!;
            var qty = item.Quantity;
            var total = cheapest.Price * qty;

            if (storeTotals.TryGetValue(cheapest.StoreId, out var existing))
                storeTotals[cheapest.StoreId] = existing with { Total = existing.Total + total, Count = existing.Count + 1 };
            else
                storeTotals[cheapest.StoreId] = (cheapest.StoreName, cheapest.ChainSlug, total, 1);

            optimizedItems.Add(BuildOptimizedItem(item, cheapest, qty, total, storePrices, altsByCategory));
        }

        return BuildResult("cheapest-total", optimizedItems, storeTotals, storeDistances, missing);
    }

    public async Task<OptimizationResult> OptimizeCheapestStoreAsync(
        Guid shoppingListId, OptimizationContext context, CancellationToken ct = default)
    {
        var (items, priceMap, storeDistances, missing, altsByCategory) = await LoadData(shoppingListId, context, ct);

        // Accumulate total cost per store
        var storeTotals = new Dictionary<Guid, decimal>();
        var storeInfo = new Dictionary<Guid, (string Name, string Slug)>();

        foreach (var item in items)
        {
            if (!priceMap.TryGetValue(item.ProductId, out var storePrices)) continue;
            foreach (var sp in storePrices)
            {
                storeTotals[sp.StoreId] = storeTotals.GetValueOrDefault(sp.StoreId) + sp.Price * item.Quantity;
                storeInfo[sp.StoreId] = (sp.StoreName, sp.ChainSlug);
            }
        }

        if (storeTotals.Count == 0)
        {
            return new OptimizationResult
            {
                Items = [],
                TotalCost = 0,
                StoreCount = 0,
                MissingItems = missing,
                OptimizationMode = "cheapest-store"
            };
        }

        var bestStoreId = storeTotals.MinBy(kv => kv.Value).Key;
        var optimizedItems = new List<OptimizedItem>();
        var winnerStoreMap = new Dictionary<Guid, (string Name, string Slug, decimal Total, int Count)>();

        foreach (var item in items)
        {
            if (!priceMap.TryGetValue(item.ProductId, out var storePrices))
            {
                missing.Add(new MissingItem { ShoppingListItemId = item.Id, ProductName = item.Product?.Name ?? "Unknown" });
                continue;
            }

            var sp = storePrices.FirstOrDefault(p => p.StoreId == bestStoreId);
            if (sp is null)
            {
                missing.Add(new MissingItem { ShoppingListItemId = item.Id, ProductName = item.Product?.Name ?? "Unknown" });
                continue;
            }

            var qty = item.Quantity;
            var total = sp.Price * qty;
            if (winnerStoreMap.TryGetValue(sp.StoreId, out var ex))
                winnerStoreMap[sp.StoreId] = ex with { Total = ex.Total + total, Count = ex.Count + 1 };
            else
                winnerStoreMap[sp.StoreId] = (sp.StoreName, sp.ChainSlug, total, 1);

            optimizedItems.Add(BuildOptimizedItem(item, sp, qty, total, storePrices, altsByCategory));
        }

        return BuildResult("cheapest-store", optimizedItems, winnerStoreMap, storeDistances, missing);
    }

    public async Task<OptimizationResult> OptimizeBalancedAsync(
        Guid shoppingListId,
        OptimizationContext context,
        decimal savingsThresholdPerStore = 2.00m,
        CancellationToken ct = default)
    {
        // Start with cheapest-total, then consolidate stores where savings < threshold
        var cheapestTotal = await OptimizeCheapestTotalAsync(shoppingListId, context, ct);
        if (cheapestTotal.Stores.Count <= 1)
        {
            return new OptimizationResult
            {
                Items = cheapestTotal.Items,
                TotalCost = cheapestTotal.TotalCost,
                StoreCount = cheapestTotal.StoreCount,
                Stores = cheapestTotal.Stores,
                MissingItems = cheapestTotal.MissingItems,
                OptimizationMode = "balanced"
            };
        }

        var (items, priceMap, storeDistances, missing, altsByCategory) = await LoadData(shoppingListId, context, ct);

        // Score each store: compute extra cost if we remove that store and reassign items
        var storeSavings = new Dictionary<Guid, decimal>();
        foreach (var store in cheapestTotal.Stores)
        {
            var otherStoreIds = cheapestTotal.Stores
                .Where(s => s.StoreId != store.StoreId)
                .Select(s => s.StoreId)
                .ToHashSet();

            decimal extraCost = 0;
            foreach (var optItem in cheapestTotal.Items.Where(i => i.StoreId == store.StoreId))
            {
                var srcItem = items.FirstOrDefault(i => i.Id == optItem.ShoppingListItemId);
                if (srcItem is null) continue;

                if (!priceMap.TryGetValue(srcItem.ProductId, out var prices)) continue;

                var bestAlternative = prices
                    .Where(p => otherStoreIds.Contains(p.StoreId))
                    .MinBy(p => p.Price);

                if (bestAlternative is null) continue; // can't consolidate this store
                extraCost += (bestAlternative.Price - optItem.UnitPrice) * optItem.Quantity;
            }

            storeSavings[store.StoreId] = store.Subtotal - extraCost;
        }

        // Remove stores where consolidating them costs less than the threshold
        var storesToRemove = storeSavings
            .Where(kv => kv.Value < savingsThresholdPerStore)
            .Select(kv => kv.Key)
            .ToHashSet();

        if (storesToRemove.Count == 0)
        {
            return new OptimizationResult
            {
                Items = cheapestTotal.Items,
                TotalCost = cheapestTotal.TotalCost,
                StoreCount = cheapestTotal.StoreCount,
                Stores = cheapestTotal.Stores,
                MissingItems = cheapestTotal.MissingItems,
                OptimizationMode = "balanced"
            };
        }

        // Re-run cheapest-total excluding removed stores
        var filteredPriceMap = priceMap.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Where(p => !storesToRemove.Contains(p.StoreId)).ToList());

        var optimizedItems = new List<OptimizedItem>();
        var storeTotals = new Dictionary<Guid, (string Name, string Slug, decimal Total, int Count)>();

        foreach (var item in items)
        {
            if (!filteredPriceMap.TryGetValue(item.ProductId, out var storePrices) || storePrices.Count == 0)
            {
                // Fall back to any store
                if (!priceMap.TryGetValue(item.ProductId, out var allPrices) || allPrices.Count == 0)
                {
                    missing.Add(new MissingItem { ShoppingListItemId = item.Id, ProductName = item.Product?.Name ?? "Unknown" });
                    continue;
                }
                storePrices = allPrices;
            }

            var cheapest = storePrices.MinBy(sp => sp.Price)!;
            var qty = item.Quantity;
            var total = cheapest.Price * qty;

            if (storeTotals.TryGetValue(cheapest.StoreId, out var existing))
                storeTotals[cheapest.StoreId] = existing with { Total = existing.Total + total, Count = existing.Count + 1 };
            else
                storeTotals[cheapest.StoreId] = (cheapest.StoreName, cheapest.ChainSlug, total, 1);

            optimizedItems.Add(BuildOptimizedItem(item, cheapest, qty, total, storePrices, altsByCategory));
        }

        return BuildResult("balanced", optimizedItems, storeTotals, storeDistances, missing);
    }

    public async Task<ComparisonMatrix> CompareAllStoresAsync(
        Guid shoppingListId, OptimizationContext context, CancellationToken ct = default)
    {
        var (items, priceMap, storeDistances, _, _) = await LoadData(shoppingListId, context, ct);

        // Collect all stores that appear in price data
        var allStores = new Dictionary<Guid, (string Name, string Slug)>();
        foreach (var prices in priceMap.Values)
            foreach (var sp in prices)
                allStores[sp.StoreId] = (sp.StoreName, sp.ChainSlug);

        var rows = new List<MatrixRow>();
        var storeTotals = allStores.ToDictionary(kv => kv.Key, _ => 0m);
        var storeMissing = allStores.ToDictionary(kv => kv.Key, _ => 0);

        foreach (var item in items)
        {
            var pricesByStore = allStores.ToDictionary(
                kv => kv.Key,
                kv => priceMap.TryGetValue(item.ProductId, out var prices)
                    ? prices.FirstOrDefault(p => p.StoreId == kv.Key)?.Price
                    : null);

            foreach (var (storeId, price) in pricesByStore)
            {
                if (price.HasValue)
                    storeTotals[storeId] += price.Value * item.Quantity;
                else
                    storeMissing[storeId]++;
            }

            decimal? cheapest = pricesByStore.Values.Where(p => p.HasValue).MinOrDefault();
            var cheapestStoreId = cheapest.HasValue
                ? pricesByStore.FirstOrDefault(kv => kv.Value == cheapest).Key
                : (Guid?)null;

            rows.Add(new MatrixRow
            {
                ShoppingListItemId = item.Id,
                ProductName = item.Product?.Name ?? "Unknown",
                Quantity = item.Quantity,
                PricesByStore = pricesByStore,
                CheapestPrice = cheapest,
                CheapestStoreId = cheapestStoreId
            });
        }

        var matrixStores = allStores.Select(kv => new MatrixStore
        {
            StoreId = kv.Key,
            Name = kv.Value.Name,
            ChainSlug = kv.Value.Slug,
            Total = storeTotals.GetValueOrDefault(kv.Key),
            MissingItems = storeMissing.GetValueOrDefault(kv.Key),
            DistanceKm = storeDistances.GetValueOrDefault(kv.Key)
        }).OrderBy(s => s.Total).ToList();

        return new ComparisonMatrix { Stores = matrixStores, Rows = rows };
    }

    // ─── Private helpers ───────────────────────────────────────────────────────

    private record StorePriceEntry(Guid StoreId, string StoreName, string ChainSlug, decimal Price, bool IsPromotion);

    private record AltProductEntry(Guid ProductId, string Name, string? Brand, Guid? CategoryId, List<StorePriceEntry> Prices);

    private async Task<(
        List<ShoppingListItem> Items,
        Dictionary<Guid, List<StorePriceEntry>> PriceMap,
        Dictionary<Guid, double> StoreDistances,
        List<MissingItem> Missing,
        Dictionary<Guid, List<AltProductEntry>> AltsByCategory)>
        LoadData(Guid shoppingListId, OptimizationContext context, CancellationToken ct)
    {
        // 1. Load shopping list items with products
        var listItems = await _db.ShoppingListItems
            .Include(i => i.Product)
            .Where(i => i.ShoppingListId == shoppingListId)
            .ToListAsync(ct);

        if (listItems.Count == 0)
            return ([], [], [], [], []);

        var productIds = listItems.Select(i => i.ProductId).ToHashSet();

        // 2. Determine which store IDs to consider
        HashSet<Guid>? allowedStoreIds = null;
        var storeDistances = new Dictionary<Guid, double>();

        if (context.StoreIds.Count > 0)
        {
            allowedStoreIds = context.StoreIds.ToHashSet();
        }
        else if (!string.IsNullOrWhiteSpace(context.PostalCode))
        {
            var coord = await _locationService.ResolvePostalCodeAsync(context.PostalCode, ct);
            if (coord is not null)
            {
                var stores = await _db.Stores
                    .Where(s => s.Latitude != null && s.Longitude != null)
                    .ToListAsync(ct);

                foreach (var store in stores)
                {
                    var dist = _locationService.CalculateDistanceKm(
                        coord.Latitude, coord.Longitude,
                        store.Latitude!.Value, store.Longitude!.Value);

                    if (dist <= context.RadiusKm)
                    {
                        storeDistances[store.Id] = dist;
                    }
                }

                if (storeDistances.Count > 0)
                    allowedStoreIds = storeDistances.Keys.ToHashSet();
            }
        }

        // 3. Load current prices for those products
        var priceQuery = _db.ProductPrices
            .Include(p => p.Store)
            .Where(p => productIds.Contains(p.ProductId) && p.IsLatest);

        if (allowedStoreIds is not null)
            priceQuery = priceQuery.Where(p => allowedStoreIds.Contains(p.StoreId));

        var prices = await priceQuery.ToListAsync(ct);

        // 4. Build price map: ProductId → list of StorePrice entries
        var priceMap = prices
            .GroupBy(p => p.ProductId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(p => new StorePriceEntry(
                    p.StoreId, p.Store?.Name ?? "Unknown", p.Store?.StoreChainId?.ToString() ?? string.Empty,
                    p.Price, p.IsPromotion))
                .ToList());

        // Also add chain slug from StoreChain if available
        var storeChainSlugs = await _db.Stores
            .Include(s => s.StoreChain)
            .Where(s => prices.Select(p => p.StoreId).Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.StoreChain?.Slug ?? string.Empty, ct);

        // Re-build with correct chain slugs
        priceMap = prices
            .GroupBy(p => p.ProductId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(p => new StorePriceEntry(
                    p.StoreId,
                    p.Store?.Name ?? "Unknown",
                    storeChainSlugs.GetValueOrDefault(p.StoreId, string.Empty),
                    p.Price,
                    p.IsPromotion))
                .ToList());

        // 5. Load true category alternatives (different ProductId, same CategoryId)
        var altsByCategory = new Dictionary<Guid, List<AltProductEntry>>();

        var categoryIds = listItems
            .Where(i => i.Product?.CategoryId != null)
            .Select(i => i.Product!.CategoryId!.Value)
            .ToHashSet();

        if (categoryIds.Count > 0)
        {
            var altPriceQuery = _db.ProductPrices
                .Include(pp => pp.Product)
                .Include(pp => pp.Store)
                    .ThenInclude(s => s!.StoreChain)
                .Where(pp => pp.IsLatest &&
                             pp.Product != null &&
                             pp.Product.CategoryId != null &&
                             categoryIds.Contains(pp.Product.CategoryId!.Value) &&
                             !productIds.Contains(pp.ProductId));

            if (allowedStoreIds is not null)
                altPriceQuery = altPriceQuery.Where(pp => allowedStoreIds.Contains(pp.StoreId));

            var altPrices = await altPriceQuery.ToListAsync(ct);

            altsByCategory = altPrices
                .GroupBy(pp => pp.Product!.CategoryId!.Value)
                .ToDictionary(
                    g => g.Key,
                    g => g.GroupBy(pp => pp.ProductId)
                          .Select(pg => new AltProductEntry(
                              pg.Key,
                              pg.First().Product!.Name,
                              pg.First().Product!.Brand,
                              pg.First().Product!.CategoryId,
                              pg.Select(pp => new StorePriceEntry(
                                  pp.StoreId,
                                  pp.Store?.Name ?? "Unknown",
                                  pp.Store?.StoreChain?.Slug ?? string.Empty,
                                  pp.Price,
                                  pp.IsPromotion))
                                .ToList()))
                          .ToList());
        }

        return (listItems, priceMap, storeDistances, [], altsByCategory);
    }

    private static OptimizedItem BuildOptimizedItem(
        ShoppingListItem item,
        StorePriceEntry cheapest,
        int qty,
        decimal total,
        List<StorePriceEntry> allPrices,
        Dictionary<Guid, List<AltProductEntry>> altsByCategory)
    {
        // Same product at other stores (price comparison)
        var sameProductAlts = allPrices
            .Where(p => p.StoreId != cheapest.StoreId)
            .Select(p => new AlternativeProduct
            {
                ProductId = item.ProductId,
                Name = item.Product?.Name ?? "Unknown",
                Brand = null,
                Price = p.Price,
                StoreName = p.StoreName,
                StoreChainSlug = p.ChainSlug,
                Savings = cheapest.Price - p.Price
            });

        // True category alternatives: different product, same category
        var categoryId = item.Product?.CategoryId;
        IEnumerable<AlternativeProduct> categoryAlts = [];
        if (categoryId is not null && altsByCategory.TryGetValue(categoryId.Value, out var altProducts))
        {
            categoryAlts = altProducts
                .Select(ap =>
                {
                    var bestPrice = ap.Prices.MinBy(p => p.Price);
                    return bestPrice is null ? null : new AlternativeProduct
                    {
                        ProductId = ap.ProductId,
                        Name = ap.Name,
                        Brand = ap.Brand,
                        Price = bestPrice.Price,
                        StoreName = bestPrice.StoreName,
                        StoreChainSlug = bestPrice.ChainSlug,
                        Savings = cheapest.Price - bestPrice.Price
                    };
                })
                .OfType<AlternativeProduct>();
        }

        var alternatives = sameProductAlts
            .Concat(categoryAlts)
            .OrderBy(a => a.Price)
            .Take(3)
            .ToList();

        return new OptimizedItem
        {
            ShoppingListItemId = item.Id,
            ProductName = item.Product?.Name ?? "Unknown",
            Quantity = qty,
            StoreId = cheapest.StoreId,
            StoreName = cheapest.StoreName,
            StoreChainSlug = cheapest.ChainSlug,
            UnitPrice = cheapest.Price,
            TotalPrice = total,
            IsPromotion = cheapest.IsPromotion,
            Alternatives = alternatives
        };
    }

    private static OptimizationResult BuildResult(
        string mode,
        List<OptimizedItem> items,
        Dictionary<Guid, (string Name, string Slug, decimal Total, int Count)> storeTotals,
        Dictionary<Guid, double> storeDistances,
        List<MissingItem> missing)
    {
        var stores = storeTotals.Select(kv => new StoreSummary
        {
            StoreId = kv.Key,
            Name = kv.Value.Name,
            ChainSlug = kv.Value.Slug,
            Subtotal = kv.Value.Total,
            ItemCount = kv.Value.Count,
            DistanceKm = storeDistances.GetValueOrDefault(kv.Key) is var d && d > 0 ? d : null
        }).OrderByDescending(s => s.Subtotal).ToList();

        return new OptimizationResult
        {
            Items = items,
            TotalCost = items.Sum(i => i.TotalPrice),
            StoreCount = storeTotals.Count,
            Stores = stores,
            MissingItems = missing,
            OptimizationMode = mode
        };
    }
}

internal static class EnumerableExtensions
{
    internal static T? MinOrDefault<T>(this IEnumerable<T?> source) where T : struct
        => source.Where(x => x.HasValue).Select(x => x!.Value).Cast<T?>().MinBy(x => x);
}
