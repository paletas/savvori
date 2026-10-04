namespace Savvori.Shared;

public class ShoppingList
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<ShoppingListItem> Items { get; set; } = new();
}

public class ShoppingListItem
{
    public Guid Id { get; set; }
    public Guid ShoppingListId { get; set; }
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public int Quantity { get; set; }
    public bool Bought { get; set; }
}

public class Product
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Brand { get; set; }
    public string? Category { get; set; }
    public Guid? CategoryId { get; set; }
    public ProductCategory? ProductCategory { get; set; }
    public string? NormalizedName { get; set; }
    /// <summary>Per-language generic names and search keywords (model-suggested, search-only); see <see cref="ProductSearchAlias"/>.</summary>
    public List<ProductSearchAlias> SearchAliases { get; set; } = new();
    public string? EAN { get; set; }
    public ProductUnit Unit { get; set; } = ProductUnit.Unit;
    public decimal? SizeValue { get; set; }
    public string? ImageUrl { get; set; }
    /// <summary>The taxonomy v1 category before the v2 migration (kept so it can be reverted).</summary>
    public Guid? LegacyCategoryId { get; set; }
    /// <summary>How the current category was set by the taxonomy migration (taxonomy-1to1, taxonomy-rule, taxonomy-left); null otherwise.</summary>
    public string? CategorySource { get; set; }
    public List<StoreProduct> StoreProducts { get; set; } = new();
}

public class Store
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Location { get; set; }
    public Guid? StoreChainId { get; set; }
    public StoreChain? StoreChain { get; set; }
    public string? Address { get; set; }
    public string? PostalCode { get; set; }
    public string? City { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public bool IsActive { get; set; } = true;
}
