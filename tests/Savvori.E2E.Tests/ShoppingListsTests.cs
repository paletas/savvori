using System.Net;
using Savvori.E2E.Tests.Infrastructure;

namespace Savvori.E2E.Tests;

/// <summary>
/// Tests shopping list management flows from an authenticated user's perspective:
/// listing, creating, renaming, deleting lists; adding/removing items;
/// and the optimization/comparison page.
/// </summary>
public class ShoppingListsTests(SavvoriWebAppFactory factory) : IClassFixture<SavvoriWebAppFactory>
{
    // ===== List Overview Page =====

    [Fact]
    public async Task ShoppingListsPage_Authenticated_ReturnsOk_ShowsLists()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/ShoppingLists");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Weekly Shopping", html);
    }

    // ===== Create List =====

    [Fact]
    public async Task CreateList_WithValidName_RedirectsToDetailPage()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var token = await SavvoriWebAppFactory.GetAntiForgeryTokenAsync(client, "/ShoppingLists");

        // MockApiHandler returns a new list with a random ID for POST /api/shoppinglists
        var noRedirectClient = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        // Re-authenticate on the non-redirect client
        var loginToken = await SavvoriWebAppFactory.GetAntiForgeryTokenAsync(noRedirectClient, "/Account/Login");
        await noRedirectClient.PostAsync("/Account/Login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Email"] = "user@savvori.test",
                ["Password"] = "TestPassword123",
                ["__RequestVerificationToken"] = loginToken
            }));

        var postToken = await SavvoriWebAppFactory.GetAntiForgeryTokenAsync(noRedirectClient, "/ShoppingLists");
        var response = await noRedirectClient.PostAsync("/ShoppingLists?handler=Create", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["name"] = "My New List",
                ["__RequestVerificationToken"] = postToken
            }));

        // After creating a list, page model redirects to /ShoppingLists/Detail/{newId}
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/ShoppingLists/Detail/", response.Headers.Location?.ToString() ?? "");
    }

    // ===== Rename List =====

    [Fact]
    public async Task RenameList_WithValidName_RedirectsBackToLists()
    {
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        var loginToken = await SavvoriWebAppFactory.GetAntiForgeryTokenAsync(client, "/Account/Login");
        await client.PostAsync("/Account/Login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Email"] = "user@savvori.test",
                ["Password"] = "TestPassword123",
                ["__RequestVerificationToken"] = loginToken
            }));

        var postToken = await SavvoriWebAppFactory.GetAntiForgeryTokenAsync(client, "/ShoppingLists");
        var response = await client.PostAsync("/ShoppingLists?handler=Rename", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["id"] = MockApiHandler.ListId.ToString(),
                ["name"] = "Renamed List",
                ["__RequestVerificationToken"] = postToken
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/ShoppingLists", response.Headers.Location?.ToString() ?? "");
    }

    // ===== Delete List =====

    [Fact]
    public async Task DeleteList_RedirectsBackToLists_ShowsSuccess()
    {
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        var loginToken = await SavvoriWebAppFactory.GetAntiForgeryTokenAsync(client, "/Account/Login");
        await client.PostAsync("/Account/Login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Email"] = "user@savvori.test",
                ["Password"] = "TestPassword123",
                ["__RequestVerificationToken"] = loginToken
            }));

        var postToken = await SavvoriWebAppFactory.GetAntiForgeryTokenAsync(client, "/ShoppingLists");
        var response = await client.PostAsync("/ShoppingLists?handler=Delete", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["id"] = MockApiHandler.ListId.ToString(),
                ["__RequestVerificationToken"] = postToken
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/ShoppingLists", response.Headers.Location?.ToString() ?? "");
    }

    // ===== List Detail Page =====

    [Fact]
    public async Task ListDetailPage_Authenticated_ReturnsOk_ShowsListName()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync($"/ShoppingLists/Detail/{MockApiHandler.ListId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Weekly Shopping", html);
    }

    [Fact]
    public async Task ListDetailPage_HtmxSearchProducts_ReturnsHtml()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/ShoppingLists/Detail/{MockApiHandler.ListId}?handler=SearchProducts&q=milk");
        request.Headers.Add("HX-Request", "true");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Test Milk 1L", html);
    }

    // ===== Optimize Page =====

    [Fact]
    public async Task OptimizePage_Authenticated_ReturnsOk()
    {
        // Without mode param — just loads the page UI without running optimization
        var client = await factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync($"/ShoppingLists/Optimize/{MockApiHandler.ListId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Weekly Shopping", html);
    }

    [Fact]
    public async Task OptimizePage_WithCheapestTotalMode_ShowsOptimizationResult()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync(
            $"/ShoppingLists/Optimize/{MockApiHandler.ListId}?mode=cheapest-total");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Continente", html);
    }

    [Fact]
    public async Task OptimizePage_WithCompareMode_ShowsComparisonMatrix()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync(
            $"/ShoppingLists/Optimize/{MockApiHandler.ListId}?mode=compare");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Continente", html);
    }
}
