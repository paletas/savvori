using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Savvori.WebApp.Services;
using Savvori.WebApp.Services.ApiModels;

namespace Savvori.WebApp.Pages.Admin.Products;

public class ProductsIndexModel(SavvoriApiClient api) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(Name = "p", SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public ProductsResponse? Result { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (PageNumber < 1) PageNumber = 1;
        Result = await api.GetProductsAsync(Search, page: PageNumber, pageSize: 20, ct: ct);
    }
}
