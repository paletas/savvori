using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Savvori.Shared;
using Savvori.WebApi.Scraping;

namespace Savvori.WebApi.Controllers;

/// <summary>
/// Admin API for inspecting and repairing product and category mappings.
/// </summary>
[ApiController]
[Route("api/admin/mapping")]
public class MappingAdminController : ControllerBase
{
    private readonly SavvoriDbContext _db;
    private readonly ILogger<MappingAdminController> _logger;

    public MappingAdminController(SavvoriDbContext db, ILogger<MappingAdminController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Returns aggregate mapping statistics.
    /// GET /api/admin/mapping/stats
    /// </summary>
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats(CancellationToken ct = default)
    {
        var totalProducts = await _db.Products.CountAsync(ct);
        var categorizedProducts = await _db.Products.CountAsync(p => p.CategoryId != null, ct);
        var uncategorizedProducts = totalProducts - categorizedProducts;

        var matchStatusCounts = await _db.StoreProducts
            .GroupBy(sp => sp.MatchStatus)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var matchMethodCounts = await _db.StoreProducts
            .Where(sp => sp.MatchMethod != null)
            .GroupBy(sp => sp.MatchMethod!)
            .Select(g => new { Method = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var unmappedCategoryCount = await _db.Products
            .Where(p => p.CategoryId == null && p.Category != null)
            .Select(p => p.Category!)
            .Distinct()
            .CountAsync(ct);

        return Ok(new
        {
            TotalProducts = totalProducts,
            CategorizedProducts = categorizedProducts,
            UncategorizedProducts = uncategorizedProducts,
            CategorizedPercent = totalProducts == 0 ? 0 : Math.Round(100.0 * categorizedProducts / totalProducts, 1),
            UnmappedCategoryStrings = unmappedCategoryCount,
            ByMatchStatus = matchStatusCounts
                .OrderBy(x => x.Status)
                .Select(x => new { Status = x.Status.ToString(), x.Count }),
            ByMatchMethod = matchMethodCounts
                .OrderByDescending(x => x.Count)
        });
    }

    /// <summary>
    /// Returns paginated canonical Products that have no CategoryId assigned.
    /// GET /api/admin/mapping/uncategorized-products?page=1&amp;pageSize=20
    /// </summary>
    [HttpGet("uncategorized-products")]
    public async Task<IActionResult> GetUncategorizedProducts(
        int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 20;

        var query = _db.Products.Where(p => p.CategoryId == null);

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderBy(p => p.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.Brand,
                RawCategory = p.Category,
                p.EAN,
                StoreProductCount = _db.StoreProducts.Count(sp => sp.CanonicalProductId == p.Id)
            })
            .ToListAsync(ct);

        return Ok(new
        {
            Page = page,
            PageSize = pageSize,
            Total = total,
            TotalPages = (int)Math.Ceiling((double)total / pageSize),
            Items = items
        });
    }

    /// <summary>
    /// Returns distinct scraped category strings that have no canonical mapping,
    /// ordered by product count descending.
    /// GET /api/admin/mapping/unmapped-categories
    /// </summary>
    [HttpGet("unmapped-categories")]
    public async Task<IActionResult> GetUnmappedCategories(CancellationToken ct = default)
    {
        var items = await _db.Products
            .Where(p => p.CategoryId == null && p.Category != null)
            .GroupBy(p => p.Category!)
            .Select(g => new { RawCategory = g.Key, ProductCount = g.Count() })
            .OrderByDescending(x => x.ProductCount)
            .ToListAsync(ct);

        // Annotate each with whether CategoryMapper now has a mapping
        var annotated = items.Select(x => new
        {
            x.RawCategory,
            x.ProductCount,
            SuggestedSlug = CategoryMapper.MapToSlug(x.RawCategory)
        });

        return Ok(annotated);
    }

    /// <summary>
    /// Returns paginated StoreProducts filtered by MatchStatus and optionally by chain.
    /// GET /api/admin/mapping/store-products?status=Unmatched&amp;chainSlug=continente&amp;page=1&amp;pageSize=20
    /// </summary>
    [HttpGet("store-products")]
    public async Task<IActionResult> GetStoreProducts(
        string? status = null,
        string? chainSlug = null,
        int page = 1,
        int pageSize = 20,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 20;

        MatchStatus? parsedStatus = null;
        if (status is not null && Enum.TryParse<MatchStatus>(status, ignoreCase: true, out var s))
            parsedStatus = s;

        var query = _db.StoreProducts
            .Include(sp => sp.StoreChain)
            .Include(sp => sp.CanonicalProduct)
            .AsQueryable();

        if (parsedStatus.HasValue)
            query = query.Where(sp => sp.MatchStatus == parsedStatus.Value);

        if (!string.IsNullOrWhiteSpace(chainSlug))
            query = query.Where(sp => sp.StoreChain.Slug == chainSlug.ToLower());

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderBy(sp => sp.MatchStatus)
            .ThenBy(sp => sp.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(sp => new
            {
                sp.Id,
                sp.Name,
                sp.Brand,
                sp.EAN,
                ChainSlug = sp.StoreChain.Slug,
                ChainName = sp.StoreChain.Name,
                MatchStatus = sp.MatchStatus.ToString(),
                sp.MatchMethod,
                sp.MatchedAt,
                sp.IsActive,
                CanonicalProductId = sp.CanonicalProductId,
                CanonicalProductName = sp.CanonicalProduct != null ? sp.CanonicalProduct.Name : null
            })
            .ToListAsync(ct);

        return Ok(new
        {
            Page = page,
            PageSize = pageSize,
            Total = total,
            TotalPages = (int)Math.Ceiling((double)total / pageSize),
            Items = items
        });
    }

    /// <summary>
    /// Re-runs CategoryMapper on all Products where CategoryId IS NULL and assigns
    /// the resolved canonical category. Returns the count of products updated.
    /// POST /api/admin/mapping/backfill-categories
    /// </summary>
    [HttpPost("backfill-categories")]
    public async Task<IActionResult> BackfillCategories(CancellationToken ct = default)
    {
        var categoryCache = await _db.ProductCategories
            .ToDictionaryAsync(pc => pc.Slug, pc => pc.Id, ct);

        var uncategorized = await _db.Products
            .Where(p => p.CategoryId == null && p.Category != null)
            .ToListAsync(ct);

        var updatedCount = 0;
        foreach (var product in uncategorized)
        {
            var slug = CategoryMapper.MapToSlug(product.Category);
            if (slug is not null && categoryCache.TryGetValue(slug, out var catId))
            {
                product.CategoryId = catId;
                updatedCount++;
            }
        }

        if (updatedCount > 0)
            await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Backfill categories: updated {Count}/{Total} products.",
            updatedCount, uncategorized.Count);

        return Ok(new { Updated = updatedCount, Skipped = uncategorized.Count - updatedCount });
    }

    /// <summary>
    /// Baseline report on how well store products are matched across chains.
    /// GET /api/admin/mapping/match-report
    /// </summary>
    [HttpGet("match-report")]
    public async Task<IActionResult> GetMatchReport(CancellationToken ct = default)
    {
        var totalStoreProducts = await _db.StoreProducts.CountAsync(ct);
        var totalCanonicals = await _db.Products.CountAsync(ct);

        // One row per store product. "Chains" counts only chains with a latest price, matching the
        // "has prices from both chains" question; the histogram counts every linked store product.
        var links = await _db.StoreProducts
            .Where(sp => sp.CanonicalProductId != null)
            .Select(sp => new
            {
                CanonicalId = sp.CanonicalProductId!.Value,
                sp.StoreChainId,
                HasLatestPrice = sp.Prices.Any(p => p.IsLatest)
            })
            .ToListAsync(ct);

        var perCanonical = links
            .GroupBy(l => l.CanonicalId)
            .ToDictionary(g => g.Key, g => (Count: g.Count(), Chains: g.Where(l => l.HasLatestPrice).Select(l => l.StoreChainId).Distinct().Count()));

        var histogram = perCanonical.Values
            .GroupBy(v => v.Count)
            .OrderBy(g => g.Key)
            .Select(g => new { StoreProducts = g.Key, Canonicals = g.Count() })
            .ToList();
        var withoutStoreProducts = totalCanonicals - perCanonical.Count;
        if (withoutStoreProducts > 0)
            histogram.Insert(0, new { StoreProducts = 0, Canonicals = withoutStoreProducts });

        var canonicalsWithNoSize = await _db.Products.CountAsync(p => p.SizeValue == null, ct);
        var canonicalsWithEan = await _db.Products.CountAsync(p => p.EAN != null && p.EAN != "", ct);
        var storeProductsWithNoSize = await _db.StoreProducts.CountAsync(sp => sp.SizeValue == null, ct);
        var storeProductsWithEan = await _db.StoreProducts.CountAsync(sp => sp.EAN != null && sp.EAN != "", ct);

        var byMatchMethod = await _db.StoreProducts
            .GroupBy(sp => sp.MatchMethod)
            .Select(g => new { Method = g.Key ?? "(none)", Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .ToListAsync(ct);

        return Ok(new
        {
            TotalStoreProducts = totalStoreProducts,
            TotalCanonicals = totalCanonicals,
            StoreProductsPerCanonical = histogram,
            CanonicalsWithMultipleChains = perCanonical.Values.Count(v => v.Chains >= 2),
            CanonicalsWithNoSize = canonicalsWithNoSize,
            StoreProductsWithNoSize = storeProductsWithNoSize,
            CanonicalsWithEan = canonicalsWithEan,
            StoreProductsWithEan = storeProductsWithEan,
            ByMatchMethod = byMatchMethod
        });
    }

    /// <summary>
    /// Re-runs Tier 1 (EAN) and Tier 2 (brand+name+size+unit) matching on StoreProducts
    /// that are Unmatched or Failed. Never creates new canonical products.
    /// POST /api/admin/mapping/rematch?chainSlug=continente
    /// </summary>
    [HttpPost("rematch")]
    public async Task<IActionResult> Rematch(string? chainSlug = null, CancellationToken ct = default)
    {
        var query = _db.StoreProducts
            .Where(sp => sp.MatchStatus == MatchStatus.Unmatched || sp.MatchStatus == MatchStatus.Failed);

        if (!string.IsNullOrWhiteSpace(chainSlug))
        {
            var chain = await _db.StoreChains.FirstOrDefaultAsync(c => c.Slug == chainSlug.ToLower(), ct);
            if (chain is null) return NotFound(new { Message = $"Chain '{chainSlug}' not found." });
            query = query.Where(sp => sp.StoreChainId == chain.Id);
        }

        var products = await query.ToListAsync(ct);
        if (products.Count == 0)
            return Ok(new { Matched = 0, Remaining = 0 });

        var matcher = new ProductMatcher(_db);
        var matchedCount = 0;

        foreach (var sp in products)
        {
            var sizeUnit = ProductNormalizer.ExtractSizeAndUnit(sp.Name);
            var sizeValue = sp.SizeValue ?? sizeUnit?.SizeValue;
            var unit = sizeUnit?.Unit ?? sp.Unit;
            var normalized = sp.NormalizedName ?? ProductNormalizer.Normalize(sp.Name);

            try
            {
                var matched = await matcher.TryMatchAsync(sp, sp.EAN, normalized, sizeValue, unit, ct);
                if (matched) matchedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Rematch failed for StoreProduct {Id}", sp.Id);
            }
        }

        if (matchedCount > 0)
            await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Rematch: matched {Matched}/{Total} store products.", matchedCount, products.Count);

        return Ok(new { Matched = matchedCount, Remaining = products.Count - matchedCount });
    }

    /// <summary>
    /// Recomputes size/unit for existing StoreProducts from the stored product name (the raw
    /// tile text is not persisted), cross-checked against the latest stored unit price.
    /// A canonical product linked to exactly one StoreProduct follows that StoreProduct's size.
    /// POST /api/admin/mapping/recompute-sizes?chainSlug=continente&amp;dryRun=true
    /// </summary>
    [HttpPost("recompute-sizes")]
    public async Task<IActionResult> RecomputeSizes(
        string? chainSlug = null, bool dryRun = false, CancellationToken ct = default)
    {
        var query = _db.StoreProducts.AsQueryable();
        if (!string.IsNullOrWhiteSpace(chainSlug))
        {
            var chain = await _db.StoreChains.FirstOrDefaultAsync(c => c.Slug == chainSlug.ToLower(), ct);
            if (chain is null) return NotFound(new { Message = $"Chain '{chainSlug}' not found." });
            query = query.Where(sp => sp.StoreChainId == chain.Id);
        }

        var latestPrices = await _db.StoreProductPrices
            .Where(p => p.IsLatest)
            .Select(p => new { p.StoreProductId, p.Price, p.UnitPrice })
            .ToDictionaryAsync(p => p.StoreProductId, ct);

        var linkedCounts = await _db.StoreProducts
            .Where(sp => sp.CanonicalProductId != null)
            .GroupBy(sp => sp.CanonicalProductId!.Value)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Id, g => g.Count, ct);

        var storeProducts = await query.Include(sp => sp.CanonicalProduct).ToListAsync(ct);
        int changed = 0, disagreements = 0, canonicalsUpdated = 0;

        foreach (var sp in storeProducts)
        {
            // The stored size may have come from a structured tile field (better than the name), so a
            // name parse only replaces it when the unit price prefers it, or, with no unit price to
            // arbitrate, when it is the stored value with a dropped decimal comma (a power-of-ten ratio).
            var parsed = ProductNormalizer.ExtractSizeAndUnit(sp.Name);
            var sizeValue = sp.SizeValue;
            var unit = sp.Unit;
            latestPrices.TryGetValue(sp.Id, out var price);

            if (parsed is { } p)
            {
                if (sp.SizeValue is null)
                {
                    sizeValue = p.SizeValue;
                    unit = p.Unit;
                }
                else if (price?.UnitPrice is not null)
                {
                    var storedOk = !ProductNormalizer.ReconcileSizeWithUnitPrice(
                        price.Price, price.UnitPrice, sp.SizeValue, sp.Unit).Disagreed;
                    var parsedOk = !ProductNormalizer.ReconcileSizeWithUnitPrice(
                        price.Price, price.UnitPrice, p.SizeValue, p.Unit).Disagreed;
                    if (!storedOk && parsedOk)
                    {
                        sizeValue = p.SizeValue;
                        unit = p.Unit;
                    }
                }
                else if (p.Unit == sp.Unit && IsPowerOfTenApart(sp.SizeValue.Value, p.SizeValue))
                {
                    sizeValue = p.SizeValue;
                }
            }

            if (price is not null)
            {
                var reconciled = ProductNormalizer.ReconcileSizeWithUnitPrice(
                    price.Price, price.UnitPrice, sizeValue, unit);
                if (reconciled.Disagreed) disagreements++;
                sizeValue = reconciled.SizeValue;
            }

            if (sizeValue == sp.SizeValue && unit == sp.Unit) continue;

            changed++;
            sp.SizeValue = sizeValue;
            sp.Unit = unit;

            var canonical = sp.CanonicalProduct;
            if (canonical is not null && linkedCounts.GetValueOrDefault(canonical.Id) == 1)
            {
                canonical.SizeValue = sizeValue;
                canonical.Unit = unit;
                canonicalsUpdated++;
            }
        }

        if (!dryRun && changed > 0)
            await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Recompute sizes (dryRun={DryRun}): {Changed}/{Total} store products changed, " +
            "{Disagreements} unit-price disagreements, {Canonicals} canonicals updated.",
            dryRun, changed, storeProducts.Count, disagreements, canonicalsUpdated);

        return Ok(new
        {
            DryRun = dryRun,
            Total = storeProducts.Count,
            Changed = changed,
            UnitPriceDisagreements = disagreements,
            CanonicalsUpdated = canonicalsUpdated
        });
    }

    /// <summary>
    /// Assigns a canonical category to a Product.
    /// PUT /api/admin/mapping/products/{id}/category
    /// Body: { "categoryId": "guid" }
    /// </summary>
    [HttpPut("products/{id:guid}/category")]
    public async Task<IActionResult> AssignProductCategory(
        Guid id,
        [FromBody] AssignCategoryRequest req,
        CancellationToken ct = default)
    {
        var product = await _db.Products.FindAsync([id], ct);
        if (product is null) return NotFound();

        var category = await _db.ProductCategories.FindAsync([req.CategoryId], ct);
        if (category is null) return BadRequest(new { Message = "Category not found." });

        product.CategoryId = req.CategoryId;
        await _db.SaveChangesAsync(ct);

        return Ok(new { product.Id, product.Name, CategoryId = req.CategoryId, CategoryName = category.Name });
    }

    /// <summary>
    /// Manually links a StoreProduct to a canonical Product.
    /// PUT /api/admin/mapping/store-products/{id}/canonical
    /// Body: { "canonicalProductId": "guid" }
    /// </summary>
    [HttpPut("store-products/{id:guid}/canonical")]
    public async Task<IActionResult> AssignCanonicalProduct(
        Guid id,
        [FromBody] AssignCanonicalRequest req,
        CancellationToken ct = default)
    {
        var storeProduct = await _db.StoreProducts.FindAsync([id], ct);
        if (storeProduct is null) return NotFound();

        var canonical = await _db.Products.FindAsync([req.CanonicalProductId], ct);
        if (canonical is null) return BadRequest(new { Message = "Canonical product not found." });

        storeProduct.CanonicalProductId = req.CanonicalProductId;
        storeProduct.MatchStatus = MatchStatus.ManualMatched;
        storeProduct.MatchMethod = "manual";
        storeProduct.MatchedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            StoreProductId = id,
            storeProduct.Name,
            CanonicalProductId = req.CanonicalProductId,
            CanonicalProductName = canonical.Name
        });
    }

    // 5 vs 0.5, 150 vs 1.5: the signature of a dropped decimal comma.
    private static bool IsPowerOfTenApart(decimal stored, decimal parsed)
    {
        if (stored <= 0 || parsed <= 0 || stored == parsed) return false;
        var ratio = (double)(stored > parsed ? stored / parsed : parsed / stored);
        var exponent = Math.Round(Math.Log10(ratio));
        return exponent >= 1 && Math.Abs(Math.Log10(ratio) - exponent) < 0.001;
    }
}

public record AssignCategoryRequest(Guid CategoryId);
public record AssignCanonicalRequest(Guid CanonicalProductId);
