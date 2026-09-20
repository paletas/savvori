using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Savvori.Shared;

namespace Savvori.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ShoppingListsController : ControllerBase
{
    private readonly SavvoriDbContext _db;

    public ShoppingListsController(SavvoriDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> GetLists()
    {
        var lists = await _db.ShoppingLists
            .Include(l => l.Items)
            .ToListAsync();
        return Ok(lists);
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
        var item = new ShoppingListItem
        {
            Id = Guid.NewGuid(),
            ShoppingListId = id,
            ProductId = req.ProductId,
            Quantity = req.Quantity
        };
        _db.ShoppingListItems.Add(item);
        await _db.SaveChangesAsync();
        return Ok(item);
    }

    [HttpDelete("{id}/items/{itemId}")]
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


    public class CreateListRequest { public string Name { get; set; } = string.Empty; }
    public class UpdateListRequest { public string Name { get; set; } = string.Empty; }
    public class AddItemRequest { public Guid ProductId { get; set; } public int Quantity { get; set; } }
}
