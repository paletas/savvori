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

    // ===== Admin Matching review =====

    [Fact]
    public async Task AdminMatchingPage_ShowsSideBySideListings_TheNeverAutoLinksNote_AndSafetyWarning()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/Admin/Matching", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("never links products by itself", html);
        Assert.DoesNotContain("Dry run", html);
        Assert.Contains("Leite Meio Gordo Mimosa 1L", html);
        Assert.Contains("Leite M. Gordo Mimosa 1L", html);
        Assert.Contains("Same product", html);
        Assert.Contains("Different variant", html);
        Assert.Contains("Not the same", html);
        Assert.Contains("probably different packs", html);
        Assert.Contains("I confirm despite the warning", html);
    }

    // ===== Admin Category suggestions =====

    [Fact]
    public async Task AdminCategorisationPage_ShowsTheSuggestOnlyNote_Suggestions_AndTheWholeStringProposal()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/Admin/Categorisation", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("never assigns a category by itself", html);
        Assert.DoesNotContain("Dry run", html);
        Assert.Contains("Uncategorised products: 42", html);
        Assert.Contains("Bolacha Maria Dourada 200g", html);
        Assert.Contains("Bolachas e Biscoitos", html);
        Assert.Contains("72", html); // confidence
        Assert.Contains("Accept", html);
        Assert.Contains("Reject", html);
        Assert.Contains("bolachas biscoitos", html);
        Assert.Contains("Apply to all", html);
        Assert.DoesNotContain("Could not load suggestions", html);
    }

    // ===== Admin bulk review =====

    [Fact]
    public async Task AdminMatchingBulkTab_ShowsTheEligibleCount_ASampleToCheck_AndAnUndoableRun()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/Admin/Matching?filter=bulk", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("885 suggestions would be applied", html);
        Assert.Contains("Spot-check a random sample first", html);
        Assert.Contains("Bombons Schoko-Bons Kinder", html);
        Assert.Contains("I checked the sample", html);
        Assert.Contains("Apply 885 suggestions", html);
        Assert.Contains("Undo this run", html);
        Assert.DoesNotContain("Could not load", html);
    }

    [Fact]
    public async Task AdminCategorisationBulkTab_ShowsTheEligibleCount_ASampleToCheck_AndAnUndoableRun()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/Admin/Categorisation?filter=bulk", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Matches(@"1.{0,8}092 predictions would be assigned", html);   // group separator depends on culture
        Assert.Contains("Azeitonas Verdes", html);
        Assert.Matches(@"Assign 1.{0,8}092 categories", html);
        Assert.Contains("Undo this run", html);
        Assert.DoesNotContain("Could not load", html);
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
