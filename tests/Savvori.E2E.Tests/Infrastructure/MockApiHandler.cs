using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Savvori.E2E.Tests.Infrastructure;

/// <summary>
/// Intercepts all outbound HTTP calls from SavvoriApiClient and returns predefined mock responses.
/// No real network calls are made.
/// </summary>
public class MockApiHandler : HttpMessageHandler
{
    public static readonly Guid CategoryId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    public static readonly Guid ProductId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    public static readonly Guid StoreId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    public static readonly Guid ListId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    public static readonly Guid ListItemId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        var query = request.RequestUri?.Query ?? "";
        var method = request.Method.Method.ToUpperInvariant();
        var pathLower = path.ToLowerInvariant();

        // ===== Admin: category suggestions =====
        if (method == "GET" && pathLower == "/api/admin/categorisation/summary")
            return Json(new
            {
                dryRun = true, uncategorised = 42,
                byStatus = new[] { new { status = "Suggested", count = 2 } },
                stringsByStatus = new[] { new { status = "Suggested", count = 1 } }
            });

        if (method == "GET" && pathLower == "/api/admin/categorisation/review")
            return Json(new
            {
                page = 1, pageSize = 20, total = 1, totalPages = 1,
                items = new[]
                {
                    new
                    {
                        id = Guid.NewGuid(), productId = Guid.NewGuid(), productName = "Bolacha Maria Dourada 200g",
                        brand = "Dourada", imageUrl = (string?)null, rawCategory = "alimentacao",
                        suggested = "Bolachas e Biscoitos", runnerUp = "Cereais e Granola", confidence = 0.72,
                        neighbourCount = 7, status = "Suggested", method = "embedding-knn"
                    }
                }
            });

        if (method == "GET" && pathLower == "/api/admin/categorisation/strings")
            return Json(new[] { new { id = Guid.NewGuid(), rawString = "bolachas biscoitos", support = 12, confidence = 0.93, category = "Bolachas e Biscoitos" } });

        if (method == "POST" && pathLower.StartsWith("/api/admin/categorisation/"))
            return Json(new { status = "ok" });

        // ===== Admin: matching review queue =====
        if (method == "GET" && pathLower == "/api/admin/matching/summary")
            return Json(new
            {
                dryRun = true,
                byStatus = new[] { new { status = "NeedsReview", count = 1 } },
                appliedByMethod = Array.Empty<object>(),
                multiChainCanonicals = 3
            });

        if (method == "GET" && pathLower == "/api/admin/matching/review")
        {
            object Listing(string chain, string name, decimal price) => new
            {
                id = Guid.NewGuid(), name, brand = "Mimosa", sizeValue = 1m, unit = "L", imageUrl = (string?)null,
                sourceUrl = (string?)null, chain, canonicalProductId = Guid.NewGuid(), price
            };
            return Json(new
            {
                page = 1, pageSize = 10, total = 1, totalPages = 1,
                items = new[]
                {
                    new
                    {
                        id = Guid.NewGuid(), cosine = 0.93, sizeKnown = true, brandCheck = "Ok", status = "NeedsReview",
                        suggestion = "embedding-cosine", verdict = (string?)null, note = (string?)null, method = (string?)null,
                        warning = "Both already have prices from the same chain: probably different packs.",
                        a = Listing("Continente", "Leite Meio Gordo Mimosa 1L", 0.89m),
                        b = Listing("Auchan", "Leite M. Gordo Mimosa 1L", 0.85m)
                    }
                }
            });
        }

        if (method == "POST" && pathLower.StartsWith("/api/admin/matching/"))
            return Json(new { status = "ok" });

        // ===== Categories =====
        if (method == "GET" && pathLower == "/api/categories")
            return Json(GetCategories());

        if (method == "GET" && pathLower.StartsWith("/api/categories/") && pathLower.EndsWith("/products"))
            return Json(GetCategoryProducts());

        if (method == "GET" && pathLower.StartsWith("/api/categories/"))
            return Json(GetCategory());

        // ===== Products =====
        if (method == "GET" && pathLower == "/api/products")
            return Json(GetProducts());

        if (method == "GET" && pathLower.StartsWith("/api/products/") && pathLower.EndsWith("/alternatives"))
            return Json(new { items = Array.Empty<object>() });

        if (method == "GET" && pathLower.StartsWith("/api/products/") && pathLower.EndsWith("/pricehistory"))
            return Json(new { productId = ProductId, storeId = (Guid?)null, days = 30, history = Array.Empty<object>() });

        if (method == "GET" && pathLower.StartsWith("/api/products/"))
            return Json(GetProduct());

        // ===== Stores =====
        if (method == "GET" && pathLower == "/api/stores")
            return Json(GetStores());

        if (method == "GET" && pathLower == "/api/stores/nearby")
            return Json(GetNearbyStores());

        if (method == "GET" && pathLower == "/api/stores/geocode")
            return Json(new { postalCode = "1000-001", latitude = 38.72, longitude = -9.14 });

        if (method == "GET" && pathLower.StartsWith("/api/stores/") && pathLower.EndsWith("/locations"))
            return Json(new
            {
                chainId = StoreId, chainSlug = "continente",
                locations = new[]
                {
                    new { id = StoreId, name = "Continente Lisboa", address = "Rua Exemplo 1", postalCode = "1000-001", city = "Lisboa", latitude = 38.72, longitude = -9.14 }
                }
            });

        // ===== Shopping Lists =====
        if (method == "GET" && pathLower == "/api/shoppinglists")
            return Json(GetShoppingLists());

        if (method == "POST" && pathLower == "/api/shoppinglists")
            return Json(CreateShoppingList(), HttpStatusCode.Created);

        // Items and optimize before generic list sub-paths to avoid wrong match order
        if (method == "POST" && pathLower.StartsWith("/api/shoppinglists/") && pathLower.EndsWith("/items"))
            return Json(new { id = ListItemId, shoppingListId = ListId, productId = ProductId, quantity = 1 });

        if (method == "DELETE" && pathLower.StartsWith("/api/shoppinglists/") && pathLower.Contains("/items/"))
            return new HttpResponseMessage(HttpStatusCode.NoContent);

        if (method == "GET" && pathLower.StartsWith("/api/shoppinglists/") && pathLower.EndsWith("/optimize"))
        {
            if (query.Contains("mode=compare"))
                return Json(GetComparisonMatrix());
            return Json(GetOptimizationResult());
        }

        if (method == "PUT" && pathLower.StartsWith("/api/shoppinglists/"))
            return Json(GetShoppingListDto());

        if (method == "DELETE" && pathLower.StartsWith("/api/shoppinglists/"))
            return new HttpResponseMessage(HttpStatusCode.NoContent);

        // ===== Admin Scraping =====
        if (method == "GET" && pathLower == "/api/admin/scraping/status")
            return Json(GetScrapingStatus());

        if (method == "GET" && pathLower.StartsWith("/api/admin/scraping/status/"))
            return Json(GetChainDetail());

        if (method == "POST" && pathLower.StartsWith("/api/admin/scraping/trigger/"))
            return Json(new { message = "Scrape queued.", chainSlug = "continente" });

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    // ---- Response helpers ----

    private static HttpResponseMessage Json(object data, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(data, JsonOpts), Encoding.UTF8, "application/json") };

    // ---- Mock data factories ----

    private static object[] GetCategories() =>
    [
        new { id = CategoryId, name = "Dairy", slug = "dairy", parentCategoryId = (Guid?)null, children = Array.Empty<object>() }
    ];

    private static object GetCategory() =>
        new { id = CategoryId, name = "Dairy", slug = "dairy", parentCategoryId = (Guid?)null, children = Array.Empty<object>() };

    private static object GetCategoryProducts() =>
        new { categoryId = CategoryId, categoryName = "Dairy", page = 1, pageSize = 20, total = 1, totalPages = 1, items = GetProductSummaries() };

    private static object[] GetProductSummaries() =>
    [
        new
        {
            id = ProductId, name = "Test Milk 1L", brand = "TestBrand", category = "Dairy",
            categoryId = CategoryId, ean = "1234567890123", unit = 0, sizeValue = (decimal?)1.0m,
            imageUrl = (string?)null, lowestPrice = (decimal?)1.49m
        }
    ];

    private static object GetProducts() =>
        new { page = 1, pageSize = 20, total = 1, totalPages = 1, items = GetProductSummaries() };

    private static object GetProduct() =>
        new
        {
            id = ProductId, name = "Test Milk 1L", brand = "TestBrand", category = "Dairy",
            categoryId = CategoryId, categoryName = "Dairy", ean = "1234567890123",
            unit = 0, sizeValue = (decimal?)1.0m, imageUrl = (string?)null, normalizedName = "milk",
            prices = new[]
            {
                new
                {
                    id = Guid.NewGuid(), storeId = StoreId, storeName = "Continente Lisboa",
                    chainSlug = "continente", price = 1.49m, unitPrice = (decimal?)1.49m,
                    currency = "EUR", isPromotion = false, promotionDescription = (string?)null,
                    sourceUrl = (string?)null, lastUpdated = DateTime.UtcNow
                }
            }
        };

    private static object[] GetStores() =>
    [
        new { id = StoreId, name = "Continente", slug = "continente", baseUrl = "https://continente.pt", logoUrl = (string?)null, isActive = true, locationCount = 5 }
    ];

    private static object GetNearbyStores() =>
        new
        {
            postalCode = "1000-001", radiusKm = 10.0, userLatitude = 38.72, userLongitude = -9.14,
            storeCount = 1, stores = new[]
            {
                new
                {
                    id = StoreId, name = "Continente Lisboa", chainSlug = "continente", chainName = "Continente",
                    address = "Rua Exemplo 1", postalCode = "1000-001", city = "Lisboa",
                    latitude = 38.72, longitude = -9.14, distanceKm = 0.5
                }
            }
        };

    private static object[] GetShoppingLists() =>
    [
        new
        {
            id = ListId, name = "Weekly Shopping",
            createdAt = DateTime.UtcNow.AddDays(-1), updatedAt = DateTime.UtcNow,
            items = new[]
            {
                new { id = ListItemId, shoppingListId = ListId, productId = ProductId, quantity = 2 }
            }
        }
    ];

    private static object CreateShoppingList() =>
        new
        {
            id = Guid.NewGuid(), name = "New List",
            createdAt = DateTime.UtcNow, updatedAt = DateTime.UtcNow, items = Array.Empty<object>()
        };

    private static object GetShoppingListDto() =>
        new
        {
            id = ListId, name = "Weekly Shopping",
            createdAt = DateTime.UtcNow.AddDays(-1), updatedAt = DateTime.UtcNow, items = Array.Empty<object>()
        };

    private static object GetOptimizationResult() =>
        new
        {
            items = new[]
            {
                new
                {
                    shoppingListItemId = ListItemId, productName = "Test Milk 1L", quantity = 2,
                    storeId = StoreId, storeName = "Continente Lisboa", storeChainSlug = "continente",
                    unitPrice = 1.49m, totalPrice = 2.98m, isPromotion = false, alternatives = Array.Empty<object>()
                }
            },
            totalCost = 2.98m, storeCount = 1,
            stores = new[]
            {
                new { storeId = StoreId, name = "Continente Lisboa", chainSlug = "continente", subtotal = 2.98m, itemCount = 1, distanceKm = (double?)null }
            },
            missingItems = Array.Empty<object>(), optimizationMode = "cheapest-total"
        };

    private static object GetComparisonMatrix() =>
        new
        {
            stores = new[]
            {
                new { storeId = StoreId, name = "Continente Lisboa", chainSlug = "continente", total = 2.98m, missingItems = 0, distanceKm = (double?)null }
            },
            rows = new[]
            {
                new
                {
                    shoppingListItemId = ListItemId, productName = "Test Milk 1L", quantity = 2,
                    pricesByStore = new Dictionary<Guid, decimal?> { { StoreId, 1.49m } },
                    cheapestPrice = (decimal?)1.49m, cheapestStoreId = (Guid?)StoreId
                }
            }
        };

    private static object[] GetScrapingStatus() =>
    [
        new
        {
            chainSlug = "continente", chainName = "Continente",
            isScheduled = true, nextFireTime = (DateTime?)DateTime.UtcNow.AddHours(6),
            lastStatus = (int?)2, lastRunAt = (DateTime?)DateTime.UtcNow.AddHours(-1),
            completedAt = (DateTime?)DateTime.UtcNow.AddMinutes(-30),
            productsScraped = 1500, errorMessage = (string?)null
        },
        new
        {
            chainSlug = "pingodoce", chainName = "Pingo Doce",
            isScheduled = true, nextFireTime = (DateTime?)DateTime.UtcNow.AddHours(6),
            lastStatus = (int?)null, lastRunAt = (DateTime?)null,
            completedAt = (DateTime?)null,
            productsScraped = 0, errorMessage = (string?)null
        }
    ];

    private static object GetChainDetail() =>
        new
        {
            chain = "continente",
            jobs = new[]
            {
                new
                {
                    id = Guid.NewGuid(), status = 2, startedAt = DateTime.UtcNow.AddHours(-1),
                    completedAt = (DateTime?)DateTime.UtcNow.AddMinutes(-30),
                    productsScraped = 1500, errorMessage = (string?)null
                }
            },
            recentLogs = new[]
            {
                new { scrapingJobId = Guid.NewGuid(), level = 2, message = "Scraping completed.", timestamp = DateTime.UtcNow.AddMinutes(-30) }
            }
        };
}
