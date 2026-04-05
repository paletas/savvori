using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Savvori.WebApp.Services;
using Savvori.WebApp.Services.ApiModels;

namespace Savvori.WebApp.Pages.Products;

public class ProductsIndexModel(SavvoriApiClient api) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? SelectedCategory { get; set; }

    [BindProperty(SupportsGet = true)]
    public new int Page { get; set; } = 1;

    public ProductsResponse? Products { get; set; }
    public List<CategoryDto> Categories { get; set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        var categoriesTask = api.GetCategoriesAsync(ct);
        var productsTask = api.GetProductsAsync(
            search: Search,
            category: SelectedCategory,
            page: Page < 1 ? 1 : Page,
            pageSize: 20,
            ct: ct);

        await Task.WhenAll(categoriesTask, productsTask);
        Categories = categoriesTask.Result;
        Products = productsTask.Result;
    }

    // HTMX handler for live search suggestions on the home page
    public async Task<IActionResult> OnGetSearchAsync(string? search, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(search) || search.Length < 2)
            return Content(string.Empty, "text/html");

        var result = await api.GetProductsAsync(search: search, pageSize: 6, ct: ct);
        var products = result?.Items ?? [];

        if (!products.Any())
            return Content("<p class='px-4 py-3 text-sm text-base-content/60'>No products found</p>", "text/html");

        var items = string.Concat(products.Select(p =>
            $"<a href=\"/Products/Detail/{p.Id}\" class=\"flex justify-between items-center px-4 py-2 hover:bg-base-200 transition-colors text-sm\">" +
            $"<span class=\"font-medium truncate mr-2\">{System.Net.WebUtility.HtmlEncode(p.Name)}</span>" +
            (p.LowestPrice.HasValue ? $"<span class=\"text-primary font-bold shrink-0\">\u20ac{p.LowestPrice.Value:F2}</span>" : "") +
            "</a>"));

        var seeAllUrl = $"/Products?search={Uri.EscapeDataString(search ?? string.Empty)}";
        var html = $"<div class='py-1'>{items}" +
                   $"<a href=\"{seeAllUrl}\" class=\"block px-4 py-2 text-xs text-primary hover:bg-base-200 border-t border-base-200\">See all results &#8594;</a></div>";

        return Content(html, "text/html");
    }
}
