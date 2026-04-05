using System.Net;
using Savvori.E2E.Tests.Infrastructure;

namespace Savvori.E2E.Tests;

/// <summary>
/// Tests public-facing pages accessible to any visitor (no auth required),
/// and verifies that protected pages redirect unauthenticated users to login.
/// </summary>
public class PublicPagesTests(SavvoriWebAppFactory factory) : IClassFixture<SavvoriWebAppFactory>
{
    // ===== Home Page =====

    [Fact]
    public async Task HomePage_ReturnsOk()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ===== Products Page =====

    [Fact]
    public async Task ProductsPage_ReturnsOk_ShowsProductList()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/Products");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Test Milk 1L", html);
    }

    [Fact]
    public async Task ProductsPage_Search_ReturnsOk_WithFilteredProducts()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/Products?search=milk");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Test Milk 1L", html);
    }

    [Fact]
    public async Task ProductsPage_HtmxSearch_ReturnsHtmlFragment()
    {
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/Products?handler=Search&search=milk");
        request.Headers.Add("HX-Request", "true");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        // Should contain product anchor links from mock data
        Assert.Contains("Test Milk 1L", html);
    }

    [Fact]
    public async Task ProductsPage_HtmxSearch_ShortQuery_ReturnsEmpty()
    {
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/Products?handler=Search&search=m");
        request.Headers.Add("HX-Request", "true");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ProductDetailPage_ReturnsOk_ShowsProductName()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync($"/Products/Detail/{MockApiHandler.ProductId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Test Milk 1L", html);
    }

    // ===== Categories Page =====

    [Fact]
    public async Task CategoriesPage_ReturnsOk_ShowsCategoryList()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/Categories");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Dairy", html);
    }

    [Fact]
    public async Task CategoryDetailPage_ReturnsOk_ShowsProducts()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/Categories/Detail/dairy");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Dairy", html);
    }

    // ===== Stores Page =====

    [Fact]
    public async Task StoresPage_ReturnsOk()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/Stores");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task StoresPage_WithPostalCode_ShowsNearbyStores()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/Stores?postalCode=1000-001&radiusKm=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Continente", html);
    }

    // ===== Auth-Protected Pages — Unauthenticated Redirect =====

    [Fact]
    public async Task ShoppingListsPage_Unauthenticated_RedirectsToLogin()
    {
        var client = factory.CreateUnauthenticatedClient();
        var response = await client.GetAsync("/ShoppingLists");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString() ?? "");
    }

    [Fact]
    public async Task ShoppingListsDetailPage_Unauthenticated_RedirectsToLogin()
    {
        var client = factory.CreateUnauthenticatedClient();
        var response = await client.GetAsync($"/ShoppingLists/Detail/{MockApiHandler.ListId}");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString() ?? "");
    }

    [Fact]
    public async Task AccountSettingsPage_Unauthenticated_RedirectsToLogin()
    {
        var client = factory.CreateUnauthenticatedClient();
        var response = await client.GetAsync("/Account/Settings");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString() ?? "");
    }

    [Fact]
    public async Task AdminPage_Unauthenticated_RedirectsToLogin()
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
}
