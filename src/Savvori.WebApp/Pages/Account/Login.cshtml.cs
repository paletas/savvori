using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Savvori.WebApp.Services;

namespace Savvori.WebApp.Pages.Account;

public class LoginModel(SavvoriApiClient api) : PageModel
{
    [BindProperty]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    public string? ErrorMessage { get; set; }

    public IActionResult OnGet(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToPage("/Index");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Email and password are required.";
            return Page();
        }

        var (success, token, isAdmin, error) = await api.LoginAsync(Email, Password);
        if (!success)
        {
            ErrorMessage = error ?? "Login failed.";
            return Page();
        }

        // Store JWT in HTTP-only cookie so AuthCookieHandler can forward it to the API
        Response.Cookies.Append("savvori_token", token!, new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddDays(7)
        });

        // Sign in with ASP.NET Core cookie auth (for [Authorize] on pages)
        var claims = new List<Claim>
        {
            new(ClaimTypes.Email, Email),
            new(ClaimTypes.Name, Email)
        };
        if (isAdmin)
            claims.Add(new(ClaimTypes.Role, "admin"));
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

        var redirect = returnUrl is not null && Url.IsLocalUrl(returnUrl) ? returnUrl : "/";
        return Redirect(redirect);
    }
}
