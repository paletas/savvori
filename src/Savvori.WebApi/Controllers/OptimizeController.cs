using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Savvori.WebApi.Services;

namespace Savvori.WebApi.Controllers;

/// <summary>
/// Shopping list price-optimization API.
/// All endpoints require authentication (the list must belong to the user).
/// </summary>
[ApiController]
[Route("api/shoppinglists/{id:guid}/optimize")]
[Authorize]
public class OptimizeController : ControllerBase
{
    private readonly SavvoriDbContext _db;
    private readonly IShoppingOptimizer _optimizer;

    public OptimizeController(SavvoriDbContext db, IShoppingOptimizer optimizer)
    {
        _db = db;
        _optimizer = optimizer;
    }

    /// <summary>
    /// Optimize a shopping list.
    ///
    /// Modes:
    ///   cheapest-total  — each item from its cheapest store (may need multiple stores)
    ///   cheapest-store  — all items from the single cheapest store
    ///   balanced        — balance cost vs. number of stores (configurable threshold)
    ///   compare         — full comparison matrix across all stores
    ///
    /// GET /api/shoppinglists/{id}/optimize?mode=cheapest-total&postalCode=1000-001&radiusKm=10
    /// GET /api/shoppinglists/{id}/optimize?mode=balanced&threshold=2.00
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Optimize(
        Guid id,
        [FromQuery] string mode = "cheapest-total",
        [FromQuery] string? postalCode = null,
        [FromQuery] double radiusKm = 15,
        [FromQuery] decimal threshold = 2.00m,
        [FromQuery] List<Guid>? storeIds = null,
        CancellationToken ct = default)
    {
        if (!await ListBelongsToUser(id, ct))
            return NotFound();

        var context = new OptimizationContext
        {
            PostalCode = postalCode,
            RadiusKm = radiusKm,
            StoreIds = storeIds ?? []
        };

        return mode.ToLower() switch
        {
            "cheapest-total" => Ok(await _optimizer.OptimizeCheapestTotalAsync(id, context, ct)),
            "cheapest-store" => Ok(await _optimizer.OptimizeCheapestStoreAsync(id, context, ct)),
            "balanced" => Ok(await _optimizer.OptimizeBalancedAsync(id, context, threshold, ct)),
            "compare" => Ok(await _optimizer.CompareAllStoresAsync(id, context, ct)),
            _ => BadRequest($"Unknown mode '{mode}'. Valid modes: cheapest-total, cheapest-store, balanced, compare")
        };
    }

    private async Task<bool> ListBelongsToUser(Guid listId, CancellationToken ct)
    {
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value;

        if (!Guid.TryParse(userIdClaim, out var userId)) return false;

        return await _db.ShoppingLists
            .AnyAsync(sl => sl.Id == listId && sl.UserId == userId, ct);
    }
}
