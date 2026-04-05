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
    public List<StoreCategory> StoreCategories { get; set; } = new();
    public List<StoreProduct> StoreProducts { get; set; } = new();
}
