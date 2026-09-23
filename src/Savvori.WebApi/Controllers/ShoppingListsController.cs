using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Savvori.Shared;

namespace Savvori.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ShoppingListsController : ControllerBase
{
    private readonly SavvoriDbContext _db;
    private readonly ProductMergeResolver _resolver;

    public ShoppingListsController(SavvoriDbContext db, ProductMergeResolver resolver)
    {
        _db = db;
        _resolver = resolver;
    }

    [HttpGet]
    public async Task<IActionResult> GetLists([FromQuery] string? name)
    {
        var query = _db.ShoppingLists.Include(l => l.Items).AsQueryable();
        if (!string.IsNullOrWhiteSpace(name))
            query = query.Where(l => l.Name == name);
        return Ok(await query.ToListAsync());
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetList(Guid id)
    {
        var list = await _db.ShoppingLists.Include(l => l.Items).FirstOrDefaultAsync(l => l.Id == id);
        return list is null ? NotFound() : Ok(list);
    }

    [HttpPost]
    public async Task<IActionResult> CreateList([FromBody] CreateListRequest req)
    {
        var list = new ShoppingList
        {
            Id = Guid.NewGuid(),
            Name = req.Name,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.ShoppingLists.Add(list);
        await _db.SaveChangesAsync();
        return Ok(list);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateList(Guid id, [FromBody] UpdateListRequest req)
    {
        var list = await _db.ShoppingLists.FirstOrDefaultAsync(l => l.Id == id);
        if (list == null) return NotFound();
        list.Name = req.Name;
        list.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(list);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteList(Guid id)
    {
        var list = await _db.ShoppingLists.FirstOrDefaultAsync(l => l.Id == id);
        if (list == null) return NotFound();
        _db.ShoppingLists.Remove(list);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id}/items")]
    public async Task<IActionResult> AddItem(Guid id, [FromBody] AddItemRequest req)
    {
        var list = await _db.ShoppingLists.FirstOrDefaultAsync(l => l.Id == id);
        if (list == null) return NotFound();

        // Resolve a stale (merged-away) product id to whatever currently owns its listings, so a
        // caller holding an old id lands on the right row instead of failing an FK insert against
        // a canonical that was deleted by MatchApplier — see ProductMergeResolver.
        var (product, _) = await _resolver.ResolveAsync(req.ProductId);
        if (product is null) return NotFound($"No product {req.ProductId} (and no merge redirects it elsewhere).");

        var exists = await _db.ShoppingListItems.AnyAsync(i => i.ShoppingListId == id && i.ProductId == product.Id);
        if (exists) return Conflict("This product is already on the list; PUT to /items/{productId} to change its quantity.");

        var item = new ShoppingListItem
        {
            Id = Guid.NewGuid(),
            ShoppingListId = id,
            ProductId = product.Id,
            Quantity = req.Quantity
        };
        _db.ShoppingListItems.Add(item);
        await _db.SaveChangesAsync();
        return Ok(ToDto(item));
    }

    /// <summary>
    /// Idempotent upsert of a line's quantity, keyed by product rather than item id — create if missing,
    /// replace the quantity outright if present. Safe to call repeatedly with the same body. Resolves a
    /// stale (merged-away) <paramref name="productId"/> to its current survivor first — the returned
    /// item's ProductId may therefore differ from the one requested; callers caching a product id
    /// (e.g. HomeOS's Item.ExternalProductId) should update their cache when that happens.
    /// </summary>
    [HttpPut("{id}/items/{productId:guid}")]
    public async Task<IActionResult> UpsertItemQuantity(Guid id, Guid productId, [FromBody] UpsertQuantityRequest req)
    {
        var list = await _db.ShoppingLists.FirstOrDefaultAsync(l => l.Id == id);
        if (list == null) return NotFound();

        var (product, _) = await _resolver.ResolveAsync(productId);
        if (product is null) return NotFound($"No product {productId} (and no merge redirects it elsewhere).");

        var item = await _db.ShoppingListItems.FirstOrDefaultAsync(i => i.ShoppingListId == id && i.ProductId == product.Id);
        if (item is null)
        {
            item = new ShoppingListItem { Id = Guid.NewGuid(), ShoppingListId = id, ProductId = product.Id, Quantity = req.Quantity };
            _db.ShoppingListItems.Add(item);
        }
        else
        {
            item.Quantity = req.Quantity;
        }
        await _db.SaveChangesAsync();
        return Ok(ToDto(item));
    }

    [HttpPut("{id}/items/{itemId:guid}/bought")]
    public async Task<IActionResult> SetItemBought(Guid id, Guid itemId, [FromBody] SetBoughtRequest req)
    {
        var item = await _db.ShoppingListItems.FirstOrDefaultAsync(i => i.Id == itemId && i.ShoppingListId == id);
        if (item == null) return NotFound();
        item.Bought = req.Bought;
        await _db.SaveChangesAsync();
        return Ok(ToDto(item));
    }

    /// <summary>Deletes every item on the list currently marked Bought.</summary>
    [HttpDelete("{id}/items/bought")]
    public async Task<IActionResult> ClearBoughtItems(Guid id)
    {
        var list = await _db.ShoppingLists.FirstOrDefaultAsync(l => l.Id == id);
        if (list == null) return NotFound();
        await _db.ShoppingListItems.Where(i => i.ShoppingListId == id && i.Bought).ExecuteDeleteAsync();
        return NoContent();
    }

    [HttpDelete("{id}/items/{itemId:guid}")]
    public async Task<IActionResult> RemoveItem(Guid id, Guid itemId)
    {
        var item = await _db.ShoppingListItems
            .FirstOrDefaultAsync(i => i.Id == itemId && i.ShoppingListId == id);
        if (item == null) return NotFound();
        var list = await _db.ShoppingLists.FirstOrDefaultAsync(l => l.Id == id);
        if (list == null) return NotFound();
        _db.ShoppingListItems.Remove(item);
        await _db.SaveChangesAsync();
        return NoContent();
    }


    // Projects to a plain DTO instead of returning the tracked entity directly. AddItem and
    // UpsertItemQuantity both resolve the product through ProductMergeResolver first, which loads
    // Product (with ProductCategory) into this same DbContext — EF's navigation fixup then wires
    // item.Product, and ProductCategory.Products loops back to include that same product, so
    // System.Text.Json throws "a possible object cycle was detected" serializing the raw entity
    // for any product that actually has a category (i.e. almost every real one). Caught in
    // production (2026-09-23): the write itself always succeeded, but the response came back 500,
    // which callers correctly treated as a failure.
    private static object ToDto(ShoppingListItem item) =>
        new { item.Id, item.ProductId, item.Quantity, item.Bought };

    public class CreateListRequest { public string Name { get; set; } = string.Empty; }
    public class UpdateListRequest { public string Name { get; set; } = string.Empty; }
    public class AddItemRequest { public Guid ProductId { get; set; } public int Quantity { get; set; } }
    public class UpsertQuantityRequest { public int Quantity { get; set; } }
    public class SetBoughtRequest { public bool Bought { get; set; } }
}
