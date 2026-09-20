using System.Net;
using Savvori.E2E.Tests.Infrastructure;

namespace Savvori.E2E.Tests;

/// <summary>
/// Tests the Admin section pages (the app has no authentication).
/// </summary>
public class AdminPagesTests(SavvoriWebAppFactory factory) : IClassFixture<SavvoriWebAppFactory>
{
    // ===== Admin Scraping =====

    [Fact]
    public async Task AdminIndexPage_Admin_ReturnsOk_ShowsAllSections()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/Admin", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Scraping", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Stores", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Products", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdminScrapingPage_Admin_ReturnsOk_ShowsJobStatus()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/Admin/Scraping", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("continente", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdminScrapingPage_HtmxRefresh_ReturnsPartialHtml()
    {
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/Admin/Scraping?handler=Refresh");
        request.Headers.Add("HX-Request", "true");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(html));
    }

    [Fact]
    public async Task AdminScrapingDetailPage_Admin_ReturnsOk_ShowsChainDetail()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/Admin/Scraping/Detail/continente", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
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


        var detailToken = await SavvoriWebAppFactory.GetAntiForgeryTokenAsync(
            client, "/Admin/Scraping/Detail/continente");

        var response = await client.PostAsync(
            "/Admin/Scraping/Detail/continente?handler=Trigger",
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["slug"] = "continente",
                    ["__RequestVerificationToken"] = detailToken
                }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Admin/Scraping/Detail", response.Headers.Location?.ToString() ?? "");
    }

    // ===== Admin Stores =====

    [Fact]
    public async Task AdminStoresPage_Admin_ReturnsOk_ShowsChains()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/Admin/Stores", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("continente", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdminStoresDetailPage_Admin_ReturnsOk_ShowsLocations()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/Admin/Stores/Detail/continente", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Lisboa", html, StringComparison.OrdinalIgnoreCase);
    }

    // ===== Admin Products =====

    [Fact]
    public async Task AdminProductsPage_Admin_ReturnsOk_ShowsProducts()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/Admin/Products", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Test Milk", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdminProductsPage_Admin_SearchFiltersResults()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/Admin/Products?search=milk", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("milk", html, StringComparison.OrdinalIgnoreCase);
    }
}
