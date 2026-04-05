using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Savvori.Shared;

namespace Savvori.WebApi.Controllers;

/// <summary>
/// Products catalog API — search, browse, and get price comparisons.
/// </summary>
[ApiController]
[Route("api/products")]
public class ProductsController : ControllerBase
{
    private readonly SavvoriDbContext _db;

    public ProductsController(SavvoriDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Search or browse products, with optional category filter.
    /// GET /api/products?search=leite&category={catId}&page=1&pageSize=20
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetProducts(
        [FromQuery] string? search,
        [FromQuery] Guid? category,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 20;

        var query = _db.Products.AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(p =>
                p.NormalizedName != null && p.NormalizedName.Contains(term) ||
                p.Name.ToLower().Contains(term) ||
                p.Brand != null && p.Brand.ToLower().Contains(term));
        }

        if (category.HasValue)
            query = query.Where(p => p.CategoryId == category.Value);

        var total = await query.CountAsync(ct);
        var products = await query
            .OrderBy(p => p.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.Brand,
                p.Category,
                p.CategoryId,
                p.EAN,
                p.Unit,
                p.SizeValue,
                p.ImageUrl,
                LowestPrice = p.StoreProducts
                    .Where(sp => sp.IsActive)
                    .SelectMany(sp => sp.Prices.Where(spp => spp.IsLatest))
                    .Select(spp => (decimal?)spp.Price)
                    .Min()
            })
            .ToListAsync(ct);

        return Ok(new
        {
            Page = page,
            PageSize = pageSize,
            Total = total,
            TotalPages = (int)Math.Ceiling((double)total / pageSize),
            Items = products
        });
    }

    /// <summary>
    /// Get a single product with prices across all store chains.
    /// GET /api/products/{id}
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetProduct(Guid id, CancellationToken ct)
    {
        var product = await _db.Products
            .Include(p => p.ProductCategory)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (product is null) return NotFound();

        var storeProducts = await _db.StoreProducts
            .Include(sp => sp.StoreChain)
            .Where(sp => sp.CanonicalProductId == id && sp.IsActive)
            .ToListAsync(ct);

        var spIds = storeProducts.Select(sp => sp.Id).ToList();
        var latestPrices = await _db.StoreProductPrices
            .Where(spp => spIds.Contains(spp.StoreProductId) && spp.IsLatest)
            .ToDictionaryAsync(spp => spp.StoreProductId, ct);

        var prices = storeProducts
            .Select(sp =>
            {
                latestPrices.TryGetValue(sp.Id, out var spp);
                return new
                {
                    Id = sp.Id,
                    StoreId = sp.StoreChainId,
                    StoreName = sp.StoreChain?.Name,
                    ChainSlug = sp.StoreChain?.Slug,
                    Price = spp?.Price ?? 0m,
                    UnitPrice = spp?.UnitPrice,
                    Currency = spp?.Currency ?? "EUR",
                    IsPromotion = spp?.IsPromotion ?? false,
                    PromotionDescription = spp?.PromotionDescription,
                    SourceUrl = sp.SourceUrl,
                    LastUpdated = spp?.ScrapedAt ?? sp.LastScraped
                };
            })
            .Where(p => p.Price > 0)
            .OrderBy(p => p.Price)
            .ToList();

        return Ok(new
        {
            product.Id,
            product.Name,
            product.Brand,
            product.Category,
            product.CategoryId,
            CategoryName = product.ProductCategory?.Name,
            product.EAN,
            product.Unit,
            product.SizeValue,
            product.ImageUrl,
            product.NormalizedName,
            Prices = prices
        });
    }

    /// <summary>
    /// Get alternative products in the same category.
    /// GET /api/products/{id}/alternatives
    /// </summary>
    [HttpGet("{id:guid}/alternatives")]
    public async Task<IActionResult> GetAlternatives(Guid id, CancellationToken ct)
    {
        var product = await _db.Products.FindAsync([id], ct);
        if (product is null) return NotFound();

        if (product.CategoryId is null)
            return Ok(new { Items = Array.Empty<object>() });

        var alternatives = await _db.Products
            .Where(p => p.CategoryId == product.CategoryId && p.Id != id)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.Brand,
                p.Unit,
                p.SizeValue,
                p.ImageUrl,
                LowestPrice = p.StoreProducts
                    .Where(sp => sp.IsActive)
                    .SelectMany(sp => sp.Prices.Where(spp => spp.IsLatest))
                    .Select(spp => (decimal?)spp.Price)
                    .Min()
            })
            .OrderBy(p => p.LowestPrice)
            .Take(10)
            .ToListAsync(ct);

        return Ok(new { Items = alternatives });
    }

    /// <summary>
    /// Get price history for a product at a specific chain.
    /// GET /api/products/{id}/pricehistory?chainSlug={slug}&days=30
    /// </summary>
    [HttpGet("{id:guid}/pricehistory")]
    public async Task<IActionResult> GetPriceHistory(
        Guid id,
        [FromQuery] string? chainSlug,
        [FromQuery] int days = 30,
        CancellationToken ct = default)
    {
        if (!await _db.Products.AnyAsync(p => p.Id == id, ct))
            return NotFound();

        var cutoff = DateTime.UtcNow.AddDays(-days);

        var storeProductQuery = _db.StoreProducts
            .Where(sp => sp.CanonicalProductId == id);

        if (!string.IsNullOrWhiteSpace(chainSlug))
        {
            storeProductQuery = storeProductQuery
                .Include(sp => sp.StoreChain)
                .Where(sp => sp.StoreChain != null && sp.StoreChain.Slug == chainSlug);
        }

        var storeProductIds = await storeProductQuery.Select(sp => sp.Id).ToListAsync(ct);

        var history = await _db.StoreProductPrices
            .Include(spp => spp.StoreProduct)
                .ThenInclude(sp => sp.StoreChain)
            .Where(spp => storeProductIds.Contains(spp.StoreProductId) && spp.ScrapedAt >= cutoff)
            .OrderBy(spp => spp.ScrapedAt)
            .Select(spp => new
            {
                spp.Id,
                StoreId = spp.StoreProduct.StoreChainId,
                StoreName = spp.StoreProduct.StoreChain != null ? spp.StoreProduct.StoreChain.Name : null,
                ChainSlug = spp.StoreProduct.StoreChain != null ? spp.StoreProduct.StoreChain.Slug : null,
                spp.Price,
                spp.UnitPrice,
                spp.IsPromotion,
                spp.IsLatest,
                LastUpdated = spp.ScrapedAt
            })
            .ToListAsync(ct);

        return Ok(new { ProductId = id, ChainSlug = chainSlug, Days = days, History = history });
    }
}
