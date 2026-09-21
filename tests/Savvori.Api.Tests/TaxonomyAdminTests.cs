using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Savvori.Api.Tests.Infrastructure;
using Savvori.Shared;
using Savvori.WebApi.Scraping;

namespace Savvori.Api.Tests;

/// <summary>The taxonomy migration against real SQLite (unique slugs, real FKs), end to end through the API.</summary>
public class TaxonomyAdminTests : IClassFixture<SavvoriWebApiFactory>
{
    private readonly SavvoriWebApiFactory _factory;
    private readonly HttpClient _client;

    public TaxonomyAdminTests(SavvoriWebApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PlanApplyRevert_EndToEnd_SwitchesTheCategoryTree_AndBack()
    {
        Guid carne = default, leite = default, vaca = default, milk = default;
        _factory.SeedData(db =>
        {
            CategorySeeder.SeedAsync(db).GetAwaiter().GetResult();
            carne = db.ProductCategories.Single(c => c.Slug == "carne").Id;
            leite = db.ProductCategories.Single(c => c.Slug == "leite").Id;
            vaca = Guid.NewGuid();
            milk = Guid.NewGuid();
            db.Products.Add(new Product { Id = vaca, Name = "Bife de Vaca 500g", CategoryId = carne });
            db.Products.Add(new Product { Id = milk, Name = "Leite Sem Lactose Bio 1L", CategoryId = leite });
        });

        // v1 tree before
        var before = await _client.GetFromJsonAsync<JsonElement>("/api/categories", Ct);
        Assert.Contains(before.EnumerateArray(), r => r.GetProperty("slug").GetString() == "laticinios");

        // dry run: changes nothing
        var plan = await _client.GetFromJsonAsync<JsonElement>("/api/admin/taxonomy/plan", Ct);
        Assert.False(plan.GetProperty("v2Active").GetBoolean());
        var carneRow = plan.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("legacySlug").GetString() == "carne");
        Assert.Equal(1, carneRow.GetProperty("byRule").GetInt32());
        _factory.SeedData(db => Assert.Null(db.Products.AsNoTracking().Single(p => p.Id == vaca).LegacyCategoryId));

        // apply
        var apply = await _client.PostAsync("/api/admin/taxonomy/apply", null, Ct);
        Assert.Equal(HttpStatusCode.OK, apply.StatusCode);
        _factory.SeedData(db =>
        {
            var p = db.Products.AsNoTracking().Single(x => x.Id == vaca);
            Assert.Equal(carne, p.LegacyCategoryId);
            Assert.Equal("carne-vaca", db.ProductCategories.Single(c => c.Id == p.CategoryId).Slug);
            var tags = db.ProductTags.Where(t => t.ProductId == milk).Select(t => t.Tag).ToList();
            Assert.Contains("bio", tags);
            Assert.Contains("sem-lactose", tags);
        });

        var after = await _client.GetFromJsonAsync<JsonElement>("/api/categories", Ct);
        var roots = after.EnumerateArray().Select(r => r.GetProperty("slug").GetString()).ToList();
        Assert.Equal(12, roots.Count);
        Assert.Contains("talho-peixaria", roots);
        Assert.DoesNotContain("laticinios", roots);

        // a second apply is refused
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync("/api/admin/taxonomy/apply", null, Ct)).StatusCode);

        // revert
        var revert = await _client.PostAsync("/api/admin/taxonomy/revert", null, Ct);
        Assert.Equal(HttpStatusCode.OK, revert.StatusCode);
        _factory.SeedData(db =>
        {
            var p = db.Products.AsNoTracking().Single(x => x.Id == vaca);
            Assert.Equal(carne, p.CategoryId);
            Assert.Null(p.LegacyCategoryId);
        });
        var reverted = await _client.GetFromJsonAsync<JsonElement>("/api/categories", Ct);
        Assert.Contains(reverted.EnumerateArray(), r => r.GetProperty("slug").GetString() == "laticinios");
        Assert.DoesNotContain(reverted.EnumerateArray(), r => r.GetProperty("slug").GetString() == "talho-peixaria");
    }
}
