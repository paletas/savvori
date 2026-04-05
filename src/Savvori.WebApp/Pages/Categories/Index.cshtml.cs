using Microsoft.AspNetCore.Mvc.RazorPages;
using Savvori.WebApp.Services;
using Savvori.WebApp.Services.ApiModels;

namespace Savvori.WebApp.Pages.Categories;

public class CategoriesIndexModel(SavvoriApiClient api) : PageModel
{
    public List<CategoryDto> Categories { get; set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Categories = await api.GetCategoriesAsync(ct);
    }
}
