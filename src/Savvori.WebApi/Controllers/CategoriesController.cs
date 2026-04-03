using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Savvori.Shared;

namespace Savvori.WebApi.Controllers;

/// <summary>
/// Product category tree API.
/// </summary>
[ApiController]
[Route("api/categories")]
public class CategoriesController : ControllerBase
{
    private readonly SavvoriDbContext _db;

    public CategoriesController(SavvoriDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Get the full category tree (all root categories with their children).
    /// GET /api/categories
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetCategories(CancellationToken ct = default)
    {
        var all = await _db.ProductCategories
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

        var roots = all
            .Where(c => c.ParentCategoryId == null)
            .Select(c => MapCategory(c, all))
            .ToList();

        return Ok(roots);
    }

    /// <summary>
    /// Get a single category by ID or slug.
    /// GET /api/categories/{idOrSlug}
    /// </summary>
    [HttpGet("{idOrSlug}")]
    public async Task<IActionResult> GetCategory(string idOrSlug, CancellationToken ct = default)
    {
        ProductCategory? category;

        if (Guid.TryParse(idOrSlug, out var id))
            category = await _db.ProductCategories.FindAsync([id], ct);
        else
            category = await _db.ProductCategories
                .FirstOrDefaultAsync(c => c.Slug == idOrSlug.ToLower(), ct);

        if (category is null) return NotFound();

        var all = await _db.ProductCategories.ToListAsync(ct);
        return Ok(MapCategory(category, all));
    }

    /// <summary>
    /// Get products in a category (with optional recursive sub-category inclusion).
    /// GET /api/categories/{idOrSlug}/products?page=1&pageSize=20&recursive=true
    /// </summary>
    [HttpGet("{idOrSlug}/products")]
    public async Task<IActionResult> GetCategoryProducts(
        string idOrSlug,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] bool recursive = false,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize is < 1 or > 100) pageSize = 20;

        ProductCategory? category;
        if (Guid.TryParse(idOrSlug, out var id))
            category = await _db.ProductCategories.FindAsync([id], ct);
        else
            category = await _db.ProductCategories
                .FirstOrDefaultAsync(c => c.Slug == idOrSlug, ct);

        if (category is null) return NotFound();

        IQueryable<Product> query;
        if (recursive)
        {
            // Collect all descendant category IDs
            var allCategories = await _db.ProductCategories.ToListAsync(ct);
            var categoryIds = GetDescendantIds(category.Id, allCategories);
            categoryIds.Add(category.Id);
            query = _db.Products.Where(p => p.CategoryId != null && categoryIds.Contains(p.CategoryId.Value));
        }
        else
        {
            query = _db.Products.Where(p => p.CategoryId == category.Id);
        }

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
            CategoryId = category.Id,
            CategoryName = category.Name,
            Page = page,
            PageSize = pageSize,
            Total = total,
            TotalPages = (int)Math.Ceiling((double)total / pageSize),
            Items = products
        });
    }

    private static object MapCategory(ProductCategory category, List<ProductCategory> all)
    {
        var children = all
            .Where(c => c.ParentCategoryId == category.Id)
            .Select(c => MapCategory(c, all))
            .ToList();

        return new
        {
            category.Id,
            category.Name,
            category.Slug,
            category.ParentCategoryId,
            Children = children
        };
    }

    private static HashSet<Guid> GetDescendantIds(Guid parentId, List<ProductCategory> all)
    {
        var result = new HashSet<Guid>();
        var directChildren = all.Where(c => c.ParentCategoryId == parentId).ToList();
        foreach (var child in directChildren)
        {
            result.Add(child.Id);
            foreach (var desc in GetDescendantIds(child.Id, all))
                result.Add(desc);
        }
        return result;
    }
}
