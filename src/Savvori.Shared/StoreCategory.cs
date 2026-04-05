namespace Savvori.Shared;

public class StoreCategory
{
    public Guid Id { get; set; }
    public Guid StoreChainId { get; set; }
    public StoreChain StoreChain { get; set; } = null!;
    /// <summary>The store's own internal category ID (e.g., a slug, numeric code, or GUID).</summary>
    public string ExternalId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Guid? ParentStoreCategoryId { get; set; }
    public StoreCategory? Parent { get; set; }
    public List<StoreCategory> Children { get; set; } = new();
    public List<StoreProduct> StoreProducts { get; set; } = new();
    public StoreCategoryMapping? Mapping { get; set; }
    public string? Url { get; set; }
    public DateTime LastScraped { get; set; }
}

/// <summary>
/// Maps a store-specific category to a canonical ProductCategory.
/// Supports hierarchical inheritance: if a leaf has no direct mapping the nearest
/// mapped ancestor's canonical category is used.
/// </summary>
public class StoreCategoryMapping
{
    public Guid Id { get; set; }
    public Guid StoreCategoryId { get; set; }
    public StoreCategory StoreCategory { get; set; } = null!;
    public Guid ProductCategoryId { get; set; }
    public ProductCategory ProductCategory { get; set; } = null!;
}
