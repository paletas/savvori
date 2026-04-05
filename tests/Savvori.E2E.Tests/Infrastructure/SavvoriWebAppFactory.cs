using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Savvori.WebApp.Services;

namespace Savvori.E2E.Tests.Infrastructure;

/// <summary>
/// WebApplicationFactory for WebApp integration tests.
/// Replaces SavvoriApiClient's primary HTTP handler with MockApiHandler so no real
/// WebApi calls are made. Cookie-based auth flows work end-to-end via the test server.
/// </summary>
public class SavvoriWebAppFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            // Override the primary (innermost) HTTP handler for SavvoriApiClient.
            // ConfigureTestServices runs after Program.cs, so this wins over the default HttpClientHandler.
            services.AddHttpClient<SavvoriApiClient>()
                .ConfigurePrimaryHttpMessageHandler(() => new MockApiHandler());
        });
    }

    /// <summary>
    /// Creates a client that does NOT follow redirects, useful for asserting redirect status codes.
    /// </summary>
    public HttpClient CreateUnauthenticatedClient() =>
        CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    /// <summary>
    /// Performs a full login flow as an admin user (email containing "admin" triggers isAdmin=true in mock).
    /// </summary>
    public Task<HttpClient> CreateAdminClientAsync() =>
        CreateAuthenticatedClientAsync("admin@savvori.test", "TestPassword123");

    /// <summary>
    /// Performs a full login flow and returns an authenticated HttpClient whose cookie container
    /// holds the savvori_auth and savvori_token cookies.
    /// </summary>
    public async Task<HttpClient> CreateAuthenticatedClientAsync(
        string email = "user@savvori.test",
        string password = "TestPassword123")
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = true,
            HandleCookies = true
        });

        var token = await GetAntiForgeryTokenAsync(client, "/Account/Login");

        await client.PostAsync("/Account/Login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Email"] = email,
                ["Password"] = password,
                ["__RequestVerificationToken"] = token
            }));

        return client;
    }

    /// <summary>
    /// GETs <paramref name="url"/> and parses the hidden __RequestVerificationToken from the response HTML.
    /// </summary>
    public static async Task<string> GetAntiForgeryTokenAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return ExtractAntiForgeryToken(await response.Content.ReadAsStringAsync());
    }

    public static string ExtractAntiForgeryToken(string html)
    {
        // Try name attribute first, then reversed attribute order
        var match = Regex.Match(html,
            @"<input[^>]+name=""__RequestVerificationToken""[^>]+value=""([^""]+)""",
            RegexOptions.IgnoreCase);

        if (!match.Success)
            match = Regex.Match(html,
                @"<input[^>]+value=""([^""]+)""[^>]+name=""__RequestVerificationToken""",
                RegexOptions.IgnoreCase);

        return match.Groups[1].Value;
    }
}
