using System.Net;
using Savvori.E2E.Tests.Infrastructure;

namespace Savvori.E2E.Tests;

/// <summary>
/// Tests the Account Settings page and delete-account flow from an authenticated user's perspective.
/// </summary>
public class AccountSettingsTests(SavvoriWebAppFactory factory) : IClassFixture<SavvoriWebAppFactory>
{
    [Fact]
    public async Task SettingsPage_Authenticated_ReturnsOk_ShowsEmail()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/Account/Settings");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        // Settings page should show the logged-in user's email
        Assert.Contains("user@savvori.test", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteAccount_SignsOutAndRedirectsToHome()
    {
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        // Authenticate first
        var loginToken = await SavvoriWebAppFactory.GetAntiForgeryTokenAsync(client, "/Account/Login");
        await client.PostAsync("/Account/Login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Email"] = "user@savvori.test",
                ["Password"] = "TestPassword123",
                ["__RequestVerificationToken"] = loginToken
            }));

        // Follow the redirect from login
        var loginRedirect = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, loginRedirect.StatusCode);

        // Get anti-forgery token from the settings page
        var settingsToken = await SavvoriWebAppFactory.GetAntiForgeryTokenAsync(client, "/Account/Settings");

        // Delete account — the Settings model signs out and redirects to /Index
        var deleteResponse = await client.PostAsync("/Account/Settings?handler=Delete", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = settingsToken
            }));

        Assert.Equal(HttpStatusCode.Redirect, deleteResponse.StatusCode);
        // After sign-out the redirect goes to the home page
        var location = deleteResponse.Headers.Location?.ToString() ?? "";
        Assert.True(location == "/" || location.EndsWith("/Index") || location.Contains("Index"),
            $"Expected redirect to home, got: {location}");
    }

    [Fact]
    public async Task SettingsPage_AfterSignOut_RedirectsToLogin()
    {
        // Sign in then sign out, then verify settings page is inaccessible
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        // 1. Authenticate
        var loginToken = await SavvoriWebAppFactory.GetAntiForgeryTokenAsync(client, "/Account/Login");
        await client.PostAsync("/Account/Login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Email"] = "user@savvori.test",
                ["Password"] = "TestPassword123",
                ["__RequestVerificationToken"] = loginToken
            }));

        // 2. Verify can reach settings
        var settingsResp = await client.GetAsync("/Account/Settings");
        Assert.Equal(HttpStatusCode.OK, settingsResp.StatusCode);

        // 3. Get token and log out
        var settingsToken = SavvoriWebAppFactory.ExtractAntiForgeryToken(await settingsResp.Content.ReadAsStringAsync());
        await client.PostAsync("/Account/Logout", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = settingsToken }));

        // 4. Settings page should now redirect to login
        var afterLogoutResponse = await client.GetAsync("/Account/Settings");
        Assert.Equal(HttpStatusCode.Redirect, afterLogoutResponse.StatusCode);
        Assert.Contains("/Account/Login", afterLogoutResponse.Headers.Location?.ToString() ?? "");
    }
}
