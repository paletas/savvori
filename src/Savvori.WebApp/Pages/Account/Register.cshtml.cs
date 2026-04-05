using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Savvori.WebApp.Services;

namespace Savvori.WebApp.Pages.Account;

public class RegisterModel(SavvoriApiClient api) : PageModel
{
    [BindProperty]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    public string ConfirmPassword { get; set; } = string.Empty;

    public string? ErrorMessage { get; set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToPage("/Index");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Email and password are required.";
            return Page();
        }

        if (Password.Length < 6)
        {
            ErrorMessage = "Password must be at least 6 characters.";
            return Page();
        }

        if (Password != ConfirmPassword)
        {
            ErrorMessage = "Passwords do not match.";
            return Page();
        }

        var (success, error) = await api.RegisterAsync(Email, Password);
        if (!success)
        {
            ErrorMessage = error ?? "Registration failed.";
            return Page();
        }

        TempData["Success"] = "Account created! Please sign in.";
        return RedirectToPage("/Account/Login");
    }
}
