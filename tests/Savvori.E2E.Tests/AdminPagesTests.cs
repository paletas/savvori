using System.Net;
using Savvori.E2E.Tests.Infrastructure;

namespace Savvori.E2E.Tests;

/// <summary>
/// Tests the Admin section from unauthenticated, non-admin, and admin perspectives.
/// Admin pages require [Authorize(Roles="admin")]; both unauthenticated and non-admin
/// users are redirected to /Account/Login (AccessDeniedPath = "/Account/Login").
/// </summary>
public class AdminPagesTests(SavvoriWebAppFactory factory) : IClassFixture<SavvoriWebAppFactory>
{
    // ===== Access Control — unauthenticated =====

    [Fact]
    public async Task AdminIndexPage_Unauthenticated_RedirectsToLogin()
    {
        var client = factory.CreateUnauthenticatedClient();
        var response = await client.GetAsync("/Admin");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString() ?? "");
    }

    [Fact]
    public async Task AdminScrapingPage_Unauthenticated_RedirectsToLogin()
    {
        var client = factory.CreateUnauthenticatedClient();
        var response = await client.GetAsync("/Admin/Scraping");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString() ?? "");
    }

    [Fact]
    public async Task AdminStoresPage_Unauthenticated_RedirectsToLogin()
    {
        var client = factory.CreateUnauthenticatedClient();
        var response = await client.GetAsync("/Admin/Stores");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString() ?? "");
    }

    [Fact]
    public async Task AdminProductsPage_Unauthenticated_RedirectsToLogin()
    {
        var client = factory.CreateUnauthenticatedClient();
        var response = await client.GetAsync("/Admin/Products");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString() ?? "");
    }

    // ===== Access Control — non-admin authenticated user =====

    [Fact]
    public async Task AdminIndexPage_NonAdmin_RedirectsToLogin()
    {
        var client = await factory.CreateAuthenticatedClientAsync("user@savvori.test");
        var unauthClient = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        // Re-login as non-admin (user@... does not have admin role)
        var token = await SavvoriWebAppFactory.GetAntiForgeryTokenAsync(unauthClient, "/Account/Login");
        await unauthClient.PostAsync("/Account/Login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Email"] = "user@savvori.test",
                ["Password"] = "TestPassword123",
                ["__RequestVerificationToken"] = token
            }));
        var response = await unauthClient.GetAsync("/Admin");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString() ?? "");
    }

    // ===== Admin Scraping =====

    [Fact]
    public async Task AdminIndexPage_Admin_ReturnsOk_ShowsAllSections()
    {
        var client = await factory.CreateAdminClientAsync();
        var response = await client.GetAsync("/Admin");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Scraping", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Stores", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Products", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdminScrapingPage_Admin_ReturnsOk_ShowsJobStatus()
    {
        var client = await factory.CreateAdminClientAsync();
        var response = await client.GetAsync("/Admin/Scraping");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("continente", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdminScrapingPage_HtmxRefresh_ReturnsPartialHtml()
    {
        var client = await factory.CreateAdminClientAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "/Admin/Scraping?handler=Refresh");
        request.Headers.Add("HX-Request", "true");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrWhiteSpace(html));
    }

    [Fact]
    public async Task AdminScrapingDetailPage_Admin_ReturnsOk_ShowsChainDetail()
    {
        var client = await factory.CreateAdminClientAsync();
        var response = await client.GetAsync("/Admin/Scraping/Detail/continente");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("continente", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdminScrapingTrigger_Admin_RedirectsBackToDetail()
    {
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        // Authenticate as admin
        var loginToken = await SavvoriWebAppFactory.GetAntiForgeryTokenAsync(client, "/Account/Login");
        await client.PostAsync("/Account/Login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Email"] = "admin@savvori.test",
                ["Password"] = "TestPassword123",
                ["__RequestVerificationToken"] = loginToken
            }));

        var detailToken = await SavvoriWebAppFactory.GetAntiForgeryTokenAsync(
            client, "/Admin/Scraping/Detail/continente");

        var response = await client.PostAsync(
            "/Admin/Scraping/Detail/continente?handler=Trigger",
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["slug"] = "continente",
                    ["__RequestVerificationToken"] = detailToken
                }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Admin/Scraping/Detail", response.Headers.Location?.ToString() ?? "");
    }

    // ===== Admin Stores =====

    [Fact]
    public async Task AdminStoresPage_Admin_ReturnsOk_ShowsChains()
    {
        var client = await factory.CreateAdminClientAsync();
        var response = await client.GetAsync("/Admin/Stores");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("continente", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdminStoresDetailPage_Admin_ReturnsOk_ShowsLocations()
    {
        var client = await factory.CreateAdminClientAsync();
        var response = await client.GetAsync("/Admin/Stores/Detail/continente");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Lisboa", html, StringComparison.OrdinalIgnoreCase);
    }

    // ===== Admin Products =====

    [Fact]
    public async Task AdminProductsPage_Admin_ReturnsOk_ShowsProducts()
    {
        var client = await factory.CreateAdminClientAsync();
        var response = await client.GetAsync("/Admin/Products");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Test Milk", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdminProductsPage_Admin_SearchFiltersResults()
    {
        var client = await factory.CreateAdminClientAsync();
        var response = await client.GetAsync("/Admin/Products?search=milk");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("milk", html, StringComparison.OrdinalIgnoreCase);
    }
}
