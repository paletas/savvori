namespace Savvori.WebApp.Services.ApiModels;

// ===== Auth =====
public record LoginResponse(string Token, bool IsAdmin);

// ===== Categories =====
public record CategoryDto(Guid Id, string Name, string Slug, Guid? ParentCategoryId, List<CategoryDto> Children);

public record CategoryProductsResponse(
    Guid CategoryId, string CategoryName, int Page, int PageSize, int Total, int TotalPages,
    List<ProductSummaryDto> Items);

// ===== Products =====
public record ProductSummaryDto(
    Guid Id, string Name, string? Brand, string? Category, Guid? CategoryId,
    string? EAN, int Unit, decimal? SizeValue, string? ImageUrl, decimal? LowestPrice);

public record ProductsResponse(int Page, int PageSize, int Total, int TotalPages, List<ProductSummaryDto> Items);

public record ProductDetailDto(
    Guid Id, string Name, string? Brand, string? Category, Guid? CategoryId, string? CategoryName,
    string? EAN, int Unit, decimal? SizeValue, string? ImageUrl, string? NormalizedName,
    List<ProductPriceDto> Prices);

public record ProductPriceDto(
    Guid Id, Guid? StoreId, string? StoreName, string? ChainSlug,
    decimal Price, decimal? UnitPrice, string? Currency,
    bool IsPromotion, string? PromotionDescription, string? SourceUrl, DateTime LastUpdated);

public record AlternativesResponse(List<ProductSummaryDto> Items);

public record PriceHistoryResponse(Guid ProductId, Guid? StoreId, int Days, List<PriceHistoryEntryDto> History);

public record PriceHistoryEntryDto(
    Guid Id, Guid? StoreId, string? StoreName, string? ChainSlug,
    decimal Price, decimal? UnitPrice, bool IsPromotion, bool IsLatest, DateTime LastUpdated);

// ===== Stores =====
public record StoreChainDto(Guid Id, string Name, string Slug, string BaseUrl, string? LogoUrl, bool IsActive, int LocationCount);

public record StoreLocationsResponse(Guid ChainId, string ChainSlug, List<StoreLocationDto> Locations);

public record StoreLocationDto(Guid Id, string Name, string? Address, string? PostalCode, string? City, double? Latitude, double? Longitude);

public record NearbyStoresResponse(
    string PostalCode, double RadiusKm, double UserLatitude, double UserLongitude,
    int StoreCount, List<NearbyStoreDto> Stores);

public record NearbyStoreDto(
    Guid Id, string Name, string? ChainSlug, string? ChainName,
    string? Address, string? PostalCode, string? City,
    double? Latitude, double? Longitude, double DistanceKm);

public record GeocodeResponse(string PostalCode, double Latitude, double Longitude);

// ===== Shopping Lists =====
public record ShoppingListDto(Guid Id, Guid UserId, string Name, DateTime CreatedAt, DateTime UpdatedAt, List<ShoppingListItemDto> Items);

public record ShoppingListItemDto(Guid Id, Guid ShoppingListId, Guid ProductId, int Quantity);

// ===== Optimization =====
public record OptimizationResultDto(
    List<OptimizedItemDto> Items, decimal TotalCost, int StoreCount,
    List<StoreSummaryDto> Stores, List<MissingItemDto> MissingItems, string OptimizationMode);

public record OptimizedItemDto(
    Guid ShoppingListItemId, string ProductName, int Quantity,
    Guid StoreId, string StoreName, string StoreChainSlug,
    decimal UnitPrice, decimal TotalPrice, bool IsPromotion,
    List<AlternativeProductDto> Alternatives);

public record AlternativeProductDto(
    Guid ProductId, string Name, string? Brand, decimal Price,
    string StoreName, string StoreChainSlug, decimal? Savings);

public record StoreSummaryDto(Guid StoreId, string Name, string ChainSlug, decimal Subtotal, int ItemCount, double? DistanceKm);

public record MissingItemDto(Guid ShoppingListItemId, string ProductName);

public record ComparisonMatrixDto(List<MatrixStoreDto> Stores, List<MatrixRowDto> Rows);

public record MatrixStoreDto(Guid StoreId, string Name, string ChainSlug, decimal Total, int MissingItems, double? DistanceKm);

public record MatrixRowDto(
    Guid ShoppingListItemId, string ProductName, int Quantity,
    Dictionary<Guid, decimal?> PricesByStore, decimal? CheapestPrice, Guid? CheapestStoreId);

// ===== Admin Scraping =====
public record ScrapingStatusDto(
    string ChainSlug, string? ChainName,
    bool IsScheduled, DateTime? NextFireTime,
    int? LastStatus, DateTime? LastRunAt, DateTime? CompletedAt,
    int ProductsScraped, string? ErrorMessage);

public record ScrapingChainDetailDto(string Chain, List<ScrapingJobDto> Jobs, List<ScrapingLogDto> RecentLogs);

public record ScrapingJobDto(
    Guid Id, int Status, DateTime StartedAt, DateTime? CompletedAt,
    int ProductsScraped, string? ErrorMessage);

public record ScrapingLogDto(Guid ScrapingJobId, int Level, string Message, DateTime Timestamp);

public record TriggerResponse(string Message, string ChainSlug);

// ===== Admin Mapping =====
public record MappingStatsDto(
    int TotalProducts,
    int CategorizedProducts,
    int UncategorizedProducts,
    double CategorizedPercent,
    int UnmappedCategoryStrings,
    List<MatchStatusCountDto> ByMatchStatus,
    List<MatchMethodCountDto> ByMatchMethod);

public record MatchStatusCountDto(string Status, int Count);
public record MatchMethodCountDto(string Method, int Count);

public record UncategorizedProductDto(
    Guid Id,
    string Name,
    string? Brand,
    string? RawCategory,
    string? EAN,
    int StoreProductCount);

public record UncategorizedProductsResponse(
    int Page, int PageSize, int Total, int TotalPages,
    List<UncategorizedProductDto> Items);

public record UnmappedCategoryDto(string RawCategory, int ProductCount, string? SuggestedSlug);

public record AdminStoreProductDto(
    Guid Id,
    string Name,
    string? Brand,
    string? EAN,
    string ChainSlug,
    string ChainName,
    string MatchStatus,
    string? MatchMethod,
    DateTime? MatchedAt,
    bool IsActive,
    Guid? CanonicalProductId,
    string? CanonicalProductName);

public record AdminStoreProductsResponse(
    int Page, int PageSize, int Total, int TotalPages,
    List<AdminStoreProductDto> Items);

public record BackfillCategoriesResponse(int Updated, int Skipped);
public record RematchResponse(int Matched, int Remaining);
