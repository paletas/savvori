namespace Savvori.Shared;

public class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string? PostalCode { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class ShoppingList
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
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
    public string? EAN { get; set; }
    public ProductUnit Unit { get; set; } = ProductUnit.Unit;
    public decimal? SizeValue { get; set; }
    public string? ImageUrl { get; set; }
    public List<ProductPrice> Prices { get; set; } = new();
    public List<ProductStoreLink> StoreLinks { get; set; } = new();
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
    public List<ProductPrice> Prices { get; set; } = new();
}

public class ProductPrice
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public Guid StoreId { get; set; }
    public Store Store { get; set; } = null!;
    public decimal Price { get; set; }
    public decimal? UnitPrice { get; set; }
    public string Currency { get; set; } = "EUR";
    public bool IsPromotion { get; set; }
    public string? PromotionDescription { get; set; }
    public string? SourceUrl { get; set; }
    public bool IsLatest { get; set; } = true;
    public DateTime LastUpdated { get; set; }
}
