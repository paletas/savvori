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
                LowestPrice = p.Prices
                    .Where(pp => pp.IsLatest)
                    .Select(pp => (decimal?)pp.Price)
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
    /// Get a single product with prices across all stores.
    /// GET /api/products/{id}
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetProduct(Guid id, CancellationToken ct)
    {
        var product = await _db.Products
            .Include(p => p.ProductCategory)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (product is null) return NotFound();

        var prices = await _db.ProductPrices
            .Include(pp => pp.Store)
                .ThenInclude(s => s!.StoreChain)
            .Where(pp => pp.ProductId == id && pp.IsLatest)
            .OrderBy(pp => pp.Price)
            .Select(pp => new
            {
                pp.Id,
                StoreId = pp.StoreId,
                StoreName = pp.Store != null ? pp.Store.Name : null,
                ChainSlug = pp.Store != null && pp.Store.StoreChain != null ? pp.Store.StoreChain.Slug : null,
                pp.Price,
                pp.UnitPrice,
                pp.Currency,
                pp.IsPromotion,
                pp.PromotionDescription,
                pp.SourceUrl,
                pp.LastUpdated
            })
            .ToListAsync(ct);

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
                LowestPrice = p.Prices
                    .Where(pp => pp.IsLatest)
                    .Select(pp => (decimal?)pp.Price)
                    .Min()
            })
            .OrderBy(p => p.LowestPrice)
            .Take(10)
            .ToListAsync(ct);

        return Ok(new { Items = alternatives });
    }

    /// <summary>
    /// Get price history for a product at a specific store.
    /// GET /api/products/{id}/pricehistory?storeId={storeId}&days=30
    /// </summary>
    [HttpGet("{id:guid}/pricehistory")]
    public async Task<IActionResult> GetPriceHistory(
        Guid id,
        [FromQuery] Guid? storeId,
        [FromQuery] int days = 30,
        CancellationToken ct = default)
    {
        if (!await _db.Products.AnyAsync(p => p.Id == id, ct))
            return NotFound();

        var cutoff = DateTime.UtcNow.AddDays(-days);

        var query = _db.ProductPrices
            .Include(pp => pp.Store)
                .ThenInclude(s => s!.StoreChain)
            .Where(pp => pp.ProductId == id && pp.LastUpdated >= cutoff);

        if (storeId.HasValue)
            query = query.Where(pp => pp.StoreId == storeId.Value);

        var history = await query
            .OrderBy(pp => pp.LastUpdated)
            .Select(pp => new
            {
                pp.Id,
                StoreId = pp.StoreId,
                StoreName = pp.Store != null ? pp.Store.Name : null,
                ChainSlug = pp.Store != null && pp.Store.StoreChain != null ? pp.Store.StoreChain.Slug : null,
                pp.Price,
                pp.UnitPrice,
                pp.IsPromotion,
                pp.IsLatest,
                pp.LastUpdated
            })
            .ToListAsync(ct);

        return Ok(new { ProductId = id, StoreId = storeId, Days = days, History = history });
    }
}
