using System.Net;
using Savvori.E2E.Tests.Infrastructure;

namespace Savvori.E2E.Tests;

/// <summary>
/// Regression tests for list-page query parameters. Razor Pages reserves the "page" route value,
/// so pagination uses "p"; the category filter uses "category".
/// </summary>
public class PaginationTests(SavvoriWebAppFactory factory) : IClassFixture<SavvoriWebAppFactory>
{
    [Fact]
    public async Task ProductsPage_PassesPageAndCategoryQueryParametersToApi()
    {
        var category = Guid.NewGuid();
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/Products?p=3&category={category}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(MockApiHandler.RequestLog,
            r => r.Contains("/api/products?") && r.Contains("page=3") && r.Contains($"category={category}"));
    }

    [Fact]
    public async Task CategoryDetailPage_PassesPageQueryParameterToApi()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/Categories/Detail/pagination-test-slug?p=4", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(MockApiHandler.RequestLog,
            r => r.Contains("/api/categories/pagination-test-slug/products") && r.Contains("page=4"));
    }

    [Fact]
    public async Task AdminProductsPage_PassesPageQueryParameterToApi()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/Admin/Products?p=5&search=paginationtest", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(MockApiHandler.RequestLog,
            r => r.Contains("/api/products?") && r.Contains("page=5") && r.Contains("search=paginationtest"));
    }

    [Fact]
    public async Task AdminMappingPage_PassesPageQueryParameterToApi()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/Admin/Mapping?tab=store-products&p=6&chainFilter=paginationtest", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(MockApiHandler.RequestLog,
            r => r.Contains("/api/admin/mapping/store-products") && r.Contains("page=6") && r.Contains("paginationtest"));
    }
}
