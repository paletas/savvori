using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Savvori.WebApp.Services;
using Savvori.WebApp.Services.ApiModels;

namespace Savvori.WebApp.Pages.ShoppingLists;

[Authorize]
public class ShoppingListDetailModel(SavvoriApiClient api, IAntiforgery antiforgery) : PageModel
{
    public Guid ListId { get; set; }
    public string? ListName { get; set; }
    public List<(ShoppingListItemDto Item, ProductSummaryDto? Product)> Items { get; set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        ListId = id;
        var lists = await api.GetShoppingListsAsync(ct);
        var list = lists.FirstOrDefault(l => l.Id == id);
        if (list is null) return NotFound();

        ListName = list.Name;

        // Enrich items with product details in parallel
        var productTasks = list.Items.Select(async item =>
        {
            var product = await api.GetProductAsync(item.ProductId, ct);
            ProductSummaryDto? summary = product is null ? null : new ProductSummaryDto(
                product.Id, product.Name, product.Brand, product.Category, product.CategoryId,
                product.EAN, product.Unit, product.SizeValue, product.ImageUrl,
                product.Prices.Select(p => (decimal?)p.Price).Min());
            return (item, summary);
        });

        Items = (await Task.WhenAll(productTasks)).ToList();
        return Page();
    }

    public async Task<IActionResult> OnGetSearchProductsAsync(Guid id, string? q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
            return Content(string.Empty, "text/html");

        var result = await api.GetProductsAsync(search: q, pageSize: 8, ct: ct);
        var products = result?.Items ?? [];

        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        var csrfToken = System.Net.WebUtility.HtmlEncode(tokens.RequestToken!);

        var html = string.Concat(products.Select(p =>
        {
            var name = System.Net.WebUtility.HtmlEncode(p.Name);
            var priceHtml = p.LowestPrice.HasValue
                ? $"<span class='text-primary font-bold ml-2 shrink-0'>€{p.LowestPrice.Value:F2}</span>"
                : string.Empty;
            return $"""
                <form method="post" action="/ShoppingLists/Detail/{id}?handler=AddItem">
                    <input type="hidden" name="__RequestVerificationToken" value="{csrfToken}" />
                    <input type="hidden" name="productId" value="{p.Id}" />
                    <input type="hidden" name="quantity" value="1" />
                    <button type="submit" class="w-full text-left px-4 py-2 hover:bg-base-200 transition-colors flex justify-between items-center text-sm">
                        <span class="font-medium truncate">{name}</span>
                        {priceHtml}
                    </button>
                </form>
                """;
        }));

        return Content(
            html.Length > 0 ? html : "<p class='px-4 py-3 text-sm text-base-content/60'>No products found</p>",
            "text/html");
    }

    public async Task<IActionResult> OnPostAddItemAsync(Guid id, Guid productId, int quantity, CancellationToken ct)
    {
        if (quantity < 1) quantity = 1;
        await api.AddItemToListAsync(id, productId, quantity, ct);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRemoveItemAsync(Guid id, Guid itemId, CancellationToken ct)
    {
        await api.RemoveItemFromListAsync(id, itemId, ct);
        return RedirectToPage(new { id });
    }
}
