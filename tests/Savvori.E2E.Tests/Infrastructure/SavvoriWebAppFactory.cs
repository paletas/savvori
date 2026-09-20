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
/// WebApi calls are made. The app has no authentication.
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
    public HttpClient CreateNoRedirectClient() =>
        CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

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
