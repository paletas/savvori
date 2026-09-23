using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Savvori.Shared;
using Savvori.WebApi.Modeling;
using Savvori.WebApi.Scraping;

namespace Savvori.WebApi.Controllers;

/// <summary>
/// Products catalog API — search, browse, and get price comparisons.
/// </summary>
[ApiController]
[Route("api/products")]
public class ProductsController : ControllerBase
{
    private readonly SavvoriDbContext _db;

    private readonly Scraping.ICategoryLocalizer _localizer;
    private readonly ProductMergeResolver _resolver;

    public ProductsController(SavvoriDbContext db, Scraping.ICategoryLocalizer localizer, ProductMergeResolver resolver)
    {
        _db = db;
        _localizer = localizer;
        _resolver = resolver;
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
            // Model-suggested per-language names ("rice" for Arroz Agulha) widen the search; they are stored
            // accent-free, so the term is folded the same way. Empty alias table = the old behaviour exactly.
            var folded = ProductNormalizer.Normalize(search);
            query = query.Where(p =>
                p.NormalizedName != null && p.NormalizedName.Contains(term) ||
                p.Name.ToLower().Contains(term) ||
                p.Brand != null && p.Brand.ToLower().Contains(term) ||
                folded != "" && p.SearchAliases.Any(a => a.SearchText.Contains(folded)));
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
        var (product, resolvedFrom) = await _resolver.ResolveAsync(id, ct);

        if (product is null) return NotFound();

        var storeProducts = await _db.StoreProducts
            .Include(sp => sp.StoreChain)
            .Where(sp => sp.CanonicalProductId == product.Id && sp.IsActive)
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
            CategoryName = product.CategoryId is { } catId
                ? (await _localizer.GetNamesAsync(_localizer.ResolveLanguage(Request), ct)).GetValueOrDefault(catId, product.ProductCategory?.Name ?? string.Empty)
                : null,
            product.EAN,
            product.Unit,
            product.SizeValue,
            product.ImageUrl,
            product.NormalizedName,
            ResolvedFrom = resolvedFrom,
            Prices = prices
        });
    }

    /// <summary>
    /// The per-language names and keywords search uses for this product (model-suggested or corrected by a person).
    /// GET /api/products/{id}/aliases
    /// </summary>
    [HttpGet("{id:guid}/aliases")]
    public async Task<IActionResult> GetAliases(Guid id, CancellationToken ct)
    {
        var (product, _) = await _resolver.ResolveAsync(id, ct);
        if (product is null) return NotFound();

        var rows = await _db.ProductSearchAliases.AsNoTracking().Where(a => a.ProductId == product.Id).ToListAsync(ct);
        return Ok(OllamaProductTranslator.Languages.Select(l =>
        {
            var row = rows.FirstOrDefault(a => a.Language == l);
            return new { Language = l, Keywords = row?.Keywords ?? string.Empty, Source = row?.Source };
        }));
    }

    public sealed record SetAliasRequest(string? Keywords);

    /// <summary>
    /// Corrects one language: replaces the keywords (comma separated) with what a person wrote. The row becomes
    /// <c>manual</c>, which the model never overwrites; empty keywords mean "no aliases in this language".
    /// PUT /api/products/{id}/aliases/{language}
    /// </summary>
    [HttpPut("{id:guid}/aliases/{language}")]
    public async Task<IActionResult> SetAlias(Guid id, string language, [FromBody] SetAliasRequest body, CancellationToken ct)
    {
        language = language.ToLowerInvariant();
        if (!OllamaProductTranslator.Languages.Contains(language))
            return BadRequest($"Language must be one of: {string.Join(", ", OllamaProductTranslator.Languages)}.");
        var (product, _) = await _resolver.ResolveAsync(id, ct);
        if (product is null) return NotFound();

        var keywords = AliasInputs.Clean((body.Keywords ?? string.Empty).Split(',', ';', '\n'));
        var row = await _db.ProductSearchAliases.FirstOrDefaultAsync(a => a.ProductId == product.Id && a.Language == language, ct);
        if (row is null)
        {
            row = new ProductSearchAlias { ProductId = product.Id, Language = language };
            _db.ProductSearchAliases.Add(row);
        }
        row.Name = keywords.FirstOrDefault() ?? string.Empty;
        row.Keywords = string.Join(", ", keywords);
        row.SearchText = ProductNormalizer.Normalize(string.Join(' ', keywords));
        row.Source = AliasInputs.ManualSource;
        row.ModelName = null;
        row.InputHash = null;
        row.CreatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { row.Language, row.Keywords, row.Source });
    }

    /// <summary>
    /// Undoes a correction: removes this language's row and marks the product's model rows out of date, so the
    /// next alias scan asks the model again (other manual corrections are kept).
    /// DELETE /api/products/{id}/aliases/{language}
    /// </summary>
    [HttpDelete("{id:guid}/aliases/{language}")]
    public async Task<IActionResult> ResetAlias(Guid id, string language, CancellationToken ct)
    {
        language = language.ToLowerInvariant();
        if (!OllamaProductTranslator.Languages.Contains(language))
            return BadRequest($"Language must be one of: {string.Join(", ", OllamaProductTranslator.Languages)}.");
        var (product, _) = await _resolver.ResolveAsync(id, ct);
        if (product is null) return NotFound();

        var rows = await _db.ProductSearchAliases.Where(a => a.ProductId == product.Id).ToListAsync(ct);
        foreach (var row in rows)
        {
            if (row.Language == language) _db.ProductSearchAliases.Remove(row);
            else if (row.Source == AliasInputs.ModelSource) row.InputHash = null; // out of date: regenerated by the next scan
        }
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Get alternative products in the same category.
    /// GET /api/products/{id}/alternatives
    /// </summary>
    [HttpGet("{id:guid}/alternatives")]
    public async Task<IActionResult> GetAlternatives(Guid id, CancellationToken ct)
    {
        var (product, resolvedFrom) = await _resolver.ResolveAsync(id, ct);
        if (product is null) return NotFound();

        if (product.CategoryId is null)
            return Ok(new { ResolvedFrom = resolvedFrom, Items = Array.Empty<object>() });

        var alternatives = await _db.Products
            .Where(p => p.CategoryId == product.CategoryId && p.Id != product.Id)
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
            // SQLite (and this provider's translation of it) sorts NULL first in ascending order,
            // so a plain OrderBy(LowestPrice) would let unpriced products crowd out priced ones —
            // rank priced products first, then order those by price.
            .OrderBy(p => p.LowestPrice == null ? 1 : 0)
            .ThenBy(p => p.LowestPrice)
            .Take(10)
            .ToListAsync(ct);

        return Ok(new { ResolvedFrom = resolvedFrom, Items = alternatives });
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
        var (product, resolvedFrom) = await _resolver.ResolveAsync(id, ct);
        if (product is null) return NotFound();

        var cutoff = DateTime.UtcNow.AddDays(-days);

        var storeProductQuery = _db.StoreProducts
            .Where(sp => sp.CanonicalProductId == product.Id);

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

        return Ok(new { ProductId = product.Id, ResolvedFrom = resolvedFrom, ChainSlug = chainSlug, Days = days, History = history });
    }
}
