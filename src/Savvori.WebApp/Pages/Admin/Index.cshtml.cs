using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Savvori.WebApp.Pages.Admin;

[Authorize(Roles = "admin")]
public class AdminIndexModel : PageModel
{
    public void OnGet() { }
}
