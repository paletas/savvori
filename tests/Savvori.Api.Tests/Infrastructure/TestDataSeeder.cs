using Savvori.Shared;
using Savvori.WebApi;

namespace Savvori.Api.Tests.Infrastructure;

/// <summary>
/// Factory methods for creating test entity objects.
/// Call db.SaveChanges() after adding entities.
/// </summary>
public static class TestDataSeeder
{
    public static User CreateTestUser(string email = "user@test.com", bool isAdmin = false) => new()
    {
        Id = Guid.NewGuid(),
        Email = email,
        PasswordHash = BCrypt.Net.BCrypt.HashPassword("Password123!"),
        IsAdmin = isAdmin,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    public static ProductCategory CreateTestCategory(
        string name,
        string? slug = null,
        Guid? parentId = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Slug = slug ?? name.ToLower().Replace(" ", "-"),
        ParentCategoryId = parentId
    };

    public static Product CreateTestProduct(
        string name,
        Guid? categoryId = null,
        string? brand = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        NormalizedName = name.ToLower(),
        Brand = brand,
        CategoryId = categoryId,
        Unit = ProductUnit.Unit
    };

    public static StoreChain CreateTestStoreChain(string name, string slug) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Slug = slug,
        BaseUrl = $"https://www.{slug}.pt",
        IsActive = true
    };

    public static Store CreateTestStore(
        Guid chainId,
        string name = "Test Store",
        double lat = 38.716,
        double lon = -9.139) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        StoreChainId = chainId,
        City = "Lisboa",
        Latitude = lat,
        Longitude = lon,
        IsActive = true
    };

    public static StoreProduct CreateTestStoreProduct(
        Guid chainId,
        Guid? canonicalProductId = null,
        string? externalId = null) => new()
    {
        Id = Guid.NewGuid(),
        StoreChainId = chainId,
        ExternalId = externalId ?? Guid.NewGuid().ToString("N"),
        Name = "Test Product",
        NormalizedName = "test product",
        Unit = ProductUnit.Unit,
        CanonicalProductId = canonicalProductId,
        MatchStatus = canonicalProductId.HasValue ? MatchStatus.AutoMatched : MatchStatus.Unmatched,
        MatchMethod = canonicalProductId.HasValue ? "created-new" : null,
        MatchedAt = canonicalProductId.HasValue ? DateTime.UtcNow : null,
        FirstSeen = DateTime.UtcNow,
        LastScraped = DateTime.UtcNow,
        IsActive = true
    };

    public static StoreProductPrice CreateTestStoreProductPrice(
        Guid storeProductId,
        decimal price,
        bool isLatest = true,
        DateTime? scrapedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        StoreProductId = storeProductId,
        Price = price,
        Currency = "EUR",
        IsLatest = isLatest,
        IsPromotion = false,
        ScrapedAt = scrapedAt ?? DateTime.UtcNow
    };

    public static ShoppingList CreateTestShoppingList(Guid userId, string name = "Test List") => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Name = name,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    public static ShoppingListItem CreateTestShoppingListItem(
        Guid listId,
        Guid productId,
        int qty = 1) => new()
    {
        Id = Guid.NewGuid(),
        ShoppingListId = listId,
        ProductId = productId,
        Quantity = qty
    };

    public static ScrapingJob CreateTestScrapingJob(
        Guid chainId,
        ScrapingJobStatus status = ScrapingJobStatus.Completed) => new()
    {
        Id = Guid.NewGuid(),
        StoreChainId = chainId,
        Status = status,
        StartedAt = DateTime.UtcNow.AddHours(-1),
        CompletedAt = status == ScrapingJobStatus.Completed ? DateTime.UtcNow.AddMinutes(-30) : null,
        ProductsScraped = status == ScrapingJobStatus.Completed ? 150 : 0
    };
}
