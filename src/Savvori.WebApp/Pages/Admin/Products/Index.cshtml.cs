using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Savvori.WebApp.Services;
using Savvori.WebApp.Services.ApiModels;

namespace Savvori.WebApp.Pages.Admin.Products;

[Authorize(Roles = "admin")]
public class ProductsIndexModel(SavvoriApiClient api) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public int Page { get; set; } = 1;

    public ProductsResponse? Result { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (Page < 1) Page = 1;
        Result = await api.GetProductsAsync(Search, page: Page, pageSize: 20, ct: ct);
    }
}
