namespace Savvori.WebApp.Services.ApiModels;

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
public record ShoppingListDto(Guid Id, string Name, DateTime CreatedAt, DateTime UpdatedAt, List<ShoppingListItemDto> Items);

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
    List<MatchMethodCountDto> ByMatchMethod,
    int MultiChainCanonicals = 0,
    List<MatchStatusCountDto>? CandidatesByStatus = null);

public record ModelStatusDto(
    bool Enabled,
    string BreakerState,
    int ConsecutiveFailures,
    DateTime? BreakerRetryAt,
    DateTime? LastSuccessAt,
    DateTime? LastErrorAt,
    string? LastError,
    int QueueDepth,
    DateTime? OldestPendingAt,
    int DeadLetterCount,
    int StaleEmbeddings,
    int ActiveProducts,
    int EmbeddedProducts,
    int Candidates,
    string EmbeddingModel,
    string JudgeModel);

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

// ===== Admin Matching (review queue) =====
public record MatchingSummaryDto(
    bool DryRun,
    List<MatchStatusCountDto> ByStatus,
    List<MatchMethodCountDto> AppliedByMethod,
    int MultiChainCanonicals);

public record ReviewListingDto(
    Guid Id, string Name, string? Brand, decimal? SizeValue, string Unit, string? ImageUrl, string? SourceUrl,
    string Chain, Guid? CanonicalProductId, decimal? Price);

public record ReviewItemDto(
    Guid Id, double Cosine, bool SizeKnown, string BrandCheck, string Status, string? Suggestion, string? Verdict,
    string? Note, string? Method, string? Warning, ReviewListingDto A, ReviewListingDto B);

public record ReviewPageDto(int Page, int PageSize, int Total, int TotalPages, List<ReviewItemDto> Items);

public record MatchingRunDto(
    string? SkippedReason, bool DryRun, int Evaluated, int AutoAccepted, int WouldAccept,
    int JudgeQueued, int SentToReview, int Blocked, int Left);

// ===== Admin Categorisation (model-suggested categories) =====
public record CategorisationSummaryDto(
    bool DryRun, int Uncategorised, List<MatchStatusCountDto> ByStatus, List<MatchStatusCountDto> StringsByStatus);

public record CategorySuggestionDto(
    Guid Id, Guid ProductId, string ProductName, string? Brand, string? ImageUrl, string? RawCategory,
    string Suggested, string? RunnerUp, double Confidence, int NeighbourCount, string Status, string Method);

public record CategorySuggestionPageDto(int Page, int PageSize, int Total, int TotalPages, List<CategorySuggestionDto> Items);

public record CategoryStringProposalDto(Guid Id, string RawString, int Support, double Confidence, string Category);

public record ClassifierRunDto(
    string? SkippedReason, bool DryRun, int Targets, int AutoAssigned, int WouldAssign, int ToReview,
    int NoSuggestion, int StringsDecided, int StringsMixed);

// ===== Admin Taxonomy v2 migration =====
public record TaxonomyPlanRowDto(
    string LegacySlug, string LegacyName, int Products, int OneToOne, int ByRule, int LeftForClassifier,
    Dictionary<string, int> RuleTargets);

public record TaxonomyPlanDto(
    bool V2Active, int ProductsWithCategory, int ProductsAlreadyMigrated, int Unchanged, int MovedToUncategorised,
    List<TaxonomyPlanRowDto> Rows, Dictionary<string, int> Tags, Dictionary<string, int>? SeedTargets = null);
public record MatchReportDto(
    int TotalStoreProducts,
    int TotalCanonicals,
    List<MatchHistogramBucketDto> StoreProductsPerCanonical,
    int CanonicalsWithMultipleChains,
    int CanonicalsWithNoSize,
    int StoreProductsWithNoSize,
    int CanonicalsWithEan,
    int StoreProductsWithEan,
    List<MatchMethodCountDto> ByMatchMethod);

public record MatchHistogramBucketDto(int StoreProducts, int Canonicals);
public record RecomputeSizesResponse(bool DryRun, int Total, int Changed, int UnitPriceDisagreements, int CanonicalsUpdated);

// ===== Bulk review =====
public record MatchBulkPreviewDto(double MinCosine, int Eligible, List<ReviewItemDto> Sample, bool Busy, double? ExactFloor = null, int EligibleExact = 0);

public record CategoryBulkSampleDto(
    Guid Id, Guid ProductId, string ProductName, string? Brand, string? ImageUrl, string? RawCategory,
    string Suggested, double Confidence, int NeighbourCount);

public record CategoryBulkPreviewDto(double MinConfidence, int Eligible, List<CategoryBulkSampleDto> Sample, bool Busy);

public record BulkBatchDto(
    Guid Id, string Method, double Threshold, string Status, int Total, int Applied, int Blocked, int Undone,
    string? Error, DateTime CreatedAt, DateTime? FinishedAt, DateTime? UndoneAt);
