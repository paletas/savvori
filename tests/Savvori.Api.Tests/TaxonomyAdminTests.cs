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
            Assert.Equal("beef", db.ProductCategories.Single(c => c.Id == p.CategoryId).Slug);
            var tags = db.ProductTags.Where(t => t.ProductId == milk).Select(t => t.Tag).ToList();
            Assert.Contains("bio", tags);
            Assert.Contains("sem-lactose", tags);
        });

        var after = await _client.GetFromJsonAsync<JsonElement>("/api/categories", Ct);
        var roots = after.EnumerateArray().Select(r => r.GetProperty("slug").GetString()).ToList();
        Assert.Equal(12, roots.Count);
        Assert.Contains("meat-fish", roots);
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
        Assert.DoesNotContain(reverted.EnumerateArray(), r => r.GetProperty("slug").GetString() == "meat-fish");
    }
}

public class CategoryLocalizationTests : IClassFixture<SavvoriWebApiFactory>
{
    private readonly SavvoriWebApiFactory _factory;
    private readonly HttpClient _client;

    public CategoryLocalizationTests(SavvoriWebApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _factory.SeedData(db =>
        {
            CategorySeeder.SeedAsync(db).GetAwaiter().GetResult();
            CategoryTranslations.SeedAsync(db).GetAwaiter().GetResult();
        });
    }

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string? NameOf(JsonElement tree, string slug)
    {
        foreach (var node in tree.EnumerateArray())
        {
            if (node.GetProperty("slug").GetString() == slug) return node.GetProperty("name").GetString();
            if (NameOf(node.GetProperty("children"), slug) is { } found) return found;
        }
        return null;
    }

    private async Task<string?> Name(string slug, Action<HttpRequestMessage>? configure = null, string url = "/api/categories")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        configure?.Invoke(request);
        var response = await _client.SendAsync(request, Ct);
        return NameOf(await response.Content.ReadFromJsonAsync<JsonElement>(Ct), slug);
    }

    [Fact]
    public async Task Names_DefaultToPortuguese()
    {
        Assert.Equal("Leite", await Name("leite"));
        Assert.Equal("Laticínios", await Name("laticinios"));
    }

    [Fact]
    public async Task LangQuery_And_AcceptLanguage_SelectEnglish_WithLangTakingPrecedence()
    {
        Assert.Equal("Milk", await Name("leite", url: "/api/categories?lang=en"));
        Assert.Equal("Milk", await Name("leite", r => r.Headers.TryAddWithoutValidation("Accept-Language", "en-GB,en;q=0.9,pt;q=0.5")));
        Assert.Equal("Leite", await Name("leite", r => r.Headers.TryAddWithoutValidation("Accept-Language", "en"), "/api/categories?lang=pt"));
    }

    [Fact]
    public async Task UnsupportedLanguages_FallBackToPortuguese_AndPtBrIsPortuguese()
    {
        Assert.Equal("Leite", await Name("leite", r => r.Headers.TryAddWithoutValidation("Accept-Language", "fr-FR,de;q=0.8")));
        Assert.Equal("Leite", await Name("leite", r => r.Headers.TryAddWithoutValidation("Accept-Language", "pt-BR")));
        Assert.Equal("Milk", await Name("leite", r => r.Headers.TryAddWithoutValidation("Accept-Language", "fr;q=0.9,en;q=0.8")));
    }

    [Fact]
    public async Task ASingleCategoryResponse_IsLocalisedToo()
    {
        var response = await _client.GetAsync("/api/categories/leite?lang=en", Ct);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal("Milk", json.GetProperty("name").GetString());
        Assert.Equal("leite", json.GetProperty("slug").GetString());   // the slug never changes with the language
    }
}
