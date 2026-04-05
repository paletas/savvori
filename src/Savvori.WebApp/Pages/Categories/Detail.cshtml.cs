using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Savvori.WebApp.Services;
using Savvori.WebApp.Services.ApiModels;

namespace Savvori.WebApp.Pages.Categories;

public class CategoryDetailModel(SavvoriApiClient api) : PageModel
{
    public string? Slug { get; set; }
    public string? CategoryName { get; set; }
    public List<ProductSummaryDto> Products { get; set; } = [];
    public int Total { get; set; }
    public int TotalPages { get; set; }
    public int CurrentPage { get; set; } = 1;

    public async Task<IActionResult> OnGetAsync(string slug, int page = 1, CancellationToken ct = default)
    {
        Slug = slug;
        CurrentPage = page;
        var result = await api.GetCategoryProductsAsync(slug, page, 20, recursive: false, ct);
        if (result is null) return NotFound();
        CategoryName = result.CategoryName;
        Products = result.Items;
        Total = result.Total;
        TotalPages = result.TotalPages;
        return Page();
    }
}
