using Microsoft.AspNetCore.Authentication.Cookies;
using Savvori.WebApp.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Cookie authentication — reads claims from the cookie set at login
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.LogoutPath = "/Account/Logout";
        options.AccessDeniedPath = "/Account/Login";
        options.Cookie.Name = "savvori_auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
    });

builder.Services.AddAuthorization();

builder.Services.AddRazorPages();
builder.Services.AddHttpContextAccessor();

// Auth handler: forwards JWT cookie to WebApi requests
builder.Services.AddTransient<AuthCookieHandler>();

// Typed HTTP client for WebApi calls
builder.Services.AddHttpClient<SavvoriApiClient>(client =>
{
    var apiUrl = builder.Configuration["services:webapi:https:0"]
        ?? builder.Configuration["services:webapi:http:0"]
        ?? "http://localhost:5000";
    client.BaseAddress = new Uri(apiUrl);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
})
.AddHttpMessageHandler<AuthCookieHandler>()
.AddStandardResilienceHandler();

var app = builder.Build();

app.MapDefaultEndpoints();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();

app.Run();

// Expose Program class for WebApplicationFactory in tests
public partial class Program { }
