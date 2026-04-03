namespace Savvori.WebApi.Services;

/// <summary>
/// Optimizes a shopping list by finding the best store combination to minimize cost.
/// </summary>
public interface IShoppingOptimizer
{
    /// <summary>
    /// Finds the cheapest prices for each item across all available stores
    /// (may require visiting multiple stores).
    /// </summary>
    Task<OptimizationResult> OptimizeCheapestTotalAsync(
        Guid shoppingListId,
        OptimizationContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Finds the single store where the total cost of the list is minimized.
    /// </summary>
    Task<OptimizationResult> OptimizeCheapestStoreAsync(
        Guid shoppingListId,
        OptimizationContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Balanced optimization: minimize cost while limiting the number of stores visited.
    /// Consolidates stores when the savings are below the threshold.
    /// </summary>
    Task<OptimizationResult> OptimizeBalancedAsync(
        Guid shoppingListId,
        OptimizationContext context,
        decimal savingsThresholdPerStore = 2.00m,
        CancellationToken ct = default);

    /// <summary>
    /// Returns a full price comparison matrix for all stores and all items.
    /// </summary>
    Task<ComparisonMatrix> CompareAllStoresAsync(
        Guid shoppingListId,
        OptimizationContext context,
        CancellationToken ct = default);
}

/// <summary>
/// Contextual parameters for optimization (user location, preferred stores, etc.).
/// </summary>
public class OptimizationContext
{
    /// <summary>Postal code to filter nearby stores. If null, all stores are considered.</summary>
    public string? PostalCode { get; init; }

    /// <summary>Maximum radius in km from the postal code. Default: 15km.</summary>
    public double RadiusKm { get; init; } = 15;

    /// <summary>Explicit list of store IDs to include. If empty, all nearby stores are used.</summary>
    public IReadOnlyList<Guid> StoreIds { get; init; } = [];
}

/// <summary>
/// The result of an optimization run.
/// </summary>
public class OptimizationResult
{
    public required IReadOnlyList<OptimizedItem> Items { get; init; }
    public decimal TotalCost { get; init; }
    public int StoreCount { get; init; }
    public IReadOnlyList<StoreSummary> Stores { get; init; } = [];
    public IReadOnlyList<MissingItem> MissingItems { get; init; } = [];
    public string OptimizationMode { get; init; } = string.Empty;
}

/// <summary>
/// A single shopping list item with its optimal purchase details.
/// </summary>
public class OptimizedItem
{
    public required Guid ShoppingListItemId { get; init; }
    public required string ProductName { get; init; }
    public int Quantity { get; init; }
    public required Guid StoreId { get; init; }
    public required string StoreName { get; init; }
    public required string StoreChainSlug { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal TotalPrice { get; init; }
    public bool IsPromotion { get; init; }
    public IReadOnlyList<AlternativeProduct> Alternatives { get; init; } = [];
}

/// <summary>
/// An alternative product suggestion (same category, different brand).
/// </summary>
public class AlternativeProduct
{
    public required Guid ProductId { get; init; }
    public required string Name { get; init; }
    public string? Brand { get; init; }
    public decimal Price { get; init; }
    public required string StoreName { get; init; }
    public required string StoreChainSlug { get; init; }
    public decimal? Savings { get; init; }
}

/// <summary>
/// Summary of a store in the optimization result.
/// </summary>
public class StoreSummary
{
    public required Guid StoreId { get; init; }
    public required string Name { get; init; }
    public required string ChainSlug { get; init; }
    public decimal Subtotal { get; init; }
    public int ItemCount { get; init; }
    public double? DistanceKm { get; init; }
}

/// <summary>
/// An item that could not be found at any available store.
/// </summary>
public class MissingItem
{
    public required Guid ShoppingListItemId { get; init; }
    public required string ProductName { get; init; }
}

/// <summary>
/// Full price comparison across all stores.
/// </summary>
public class ComparisonMatrix
{
    public required IReadOnlyList<MatrixStore> Stores { get; init; }
    public required IReadOnlyList<MatrixRow> Rows { get; init; }
}

/// <summary>
/// A store column in the comparison matrix.
/// </summary>
public class MatrixStore
{
    public required Guid StoreId { get; init; }
    public required string Name { get; init; }
    public required string ChainSlug { get; init; }
    public decimal Total { get; init; }
    public int MissingItems { get; init; }
    public double? DistanceKm { get; init; }
}

/// <summary>
/// A row in the comparison matrix (one per shopping list item).
/// </summary>
public class MatrixRow
{
    public required Guid ShoppingListItemId { get; init; }
    public required string ProductName { get; init; }
    public int Quantity { get; init; }
    /// <summary>Key: StoreId → price (null if not available at that store).</summary>
    public required IReadOnlyDictionary<Guid, decimal?> PricesByStore { get; init; }
    public decimal? CheapestPrice { get; init; }
    public Guid? CheapestStoreId { get; init; }
}
