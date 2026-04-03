namespace Savvori.Shared;

public class StoreChain
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string? LogoUrl { get; set; }
    public bool IsActive { get; set; } = true;
    public List<Store> Locations { get; set; } = new();
    public List<ScrapingJob> ScrapingJobs { get; set; } = new();
}

public class ProductStoreLink
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public Guid StoreChainId { get; set; }
    public StoreChain StoreChain { get; set; } = null!;
    /// <summary>The store's own internal product identifier (e.g., SKU, URL slug).</summary>
    public string ExternalId { get; set; } = string.Empty;
    public string? SourceUrl { get; set; }
    public DateTime LastSeen { get; set; }
}
