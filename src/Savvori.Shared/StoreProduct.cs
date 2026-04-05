namespace Savvori.Shared;

/// <summary>
/// A raw product as seen by a specific store chain's scraper.
/// Linked to a canonical Product once matched.
/// </summary>
public class StoreProduct
{
    public Guid Id { get; set; }
    public Guid StoreChainId { get; set; }
    public StoreChain StoreChain { get; set; } = null!;
    /// <summary>The store's own internal product identifier (SKU, URL slug, numeric ID, etc.).</summary>
    public string ExternalId { get; set; } = string.Empty;
    public Guid? StoreCategoryId { get; set; }
    public StoreCategory? StoreCategory { get; set; }
    /// <summary>Linked canonical product once matched; null until matching runs.</summary>
    public Guid? CanonicalProductId { get; set; }
    public Product? CanonicalProduct { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? NormalizedName { get; set; }
    public string? Brand { get; set; }
    public string? EAN { get; set; }
    public string? ImageUrl { get; set; }
    public string? SourceUrl { get; set; }
    public ProductUnit Unit { get; set; } = ProductUnit.Unit;
    public decimal? SizeValue { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastScraped { get; set; }
    /// <summary>False if the product was not seen in the most recent scrape for this chain.</summary>
    public bool IsActive { get; set; } = true;
    public MatchStatus MatchStatus { get; set; } = MatchStatus.Unmatched;
    /// <summary>e.g. "ean", "brand+name+size+unit", "created-new"</summary>
    public string? MatchMethod { get; set; }
    public DateTime? MatchedAt { get; set; }
    public List<StoreProductPrice> Prices { get; set; } = new();
}

/// <summary>
/// A price snapshot for a StoreProduct at a specific point in time.
/// IsLatest=true indicates the current price (enforced by unique partial index).
/// </summary>
public class StoreProductPrice
{
    public Guid Id { get; set; }
    public Guid StoreProductId { get; set; }
    public StoreProduct StoreProduct { get; set; } = null!;
    public decimal Price { get; set; }
    public decimal? UnitPrice { get; set; }
    public string Currency { get; set; } = "EUR";
    public bool IsPromotion { get; set; }
    /// <summary>Raw promotion text from the store website (e.g., "Desconto 0.50€").</summary>
    public string? PromotionDescription { get; set; }
    public bool IsLatest { get; set; } = true;
    public DateTime ScrapedAt { get; set; }
}
