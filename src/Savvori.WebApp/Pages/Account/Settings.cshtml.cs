using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Savvori.WebApp.Pages.Account;

[Authorize]
public class SettingsModel : PageModel
{
    public void OnGet() { }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        Response.Cookies.Delete("savvori_token");
        TempData["Success"] = "Your account has been deleted.";
        return RedirectToPage("/Index");
    }
}
