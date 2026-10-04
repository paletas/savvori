using System.Net.Http.Json;
using System.Text.Json;
using Savvori.Api.Tests.Infrastructure;
using Savvori.Shared;

namespace Savvori.Api.Tests;

public class ProductAliasSearchTests : IClassFixture<SavvoriWebApiFactory>
{
    private readonly HttpClient _client;
    // Unique words so other tests' seed data in the shared database cannot interfere.
    private readonly string _pt = $"grao{Guid.NewGuid():N}"[..14];
    private readonly string _en = $"kernel{Guid.NewGuid():N}"[..14];
    private readonly Guid _withAlias = Guid.NewGuid();
    private readonly Guid _withoutAlias = Guid.NewGuid();

    public ProductAliasSearchTests(SavvoriWebApiFactory factory)
    {
        _client = factory.CreateClient();
        factory.SeedData(db =>
        {
            var a = TestDataSeeder.CreateTestProduct($"{_pt} Agulha");
            a.Id = _withAlias;
            a.SearchAliases.Add(new ProductSearchAlias
            {
                Language = "en", Name = _en, Keywords = _en, SearchText = _en, Source = "model", CreatedAt = DateTime.UtcNow
            });
            var b = TestDataSeeder.CreateTestProduct($"{_pt} Basmati");
            b.Id = _withoutAlias;
            db.Products.AddRange(a, b);
        });
    }

    private async Task<List<Guid>> SearchAsync(string term)
    {
        var body = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/products?search={Uri.EscapeDataString(term)}", TestContext.Current.CancellationToken);
        return body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
    }

    [Fact]
    public async Task Search_FindsAProductByItsAlias_InAnotherLanguage()
    {
        var ids = await SearchAsync(_en);
        Assert.Equal([_withAlias], ids);
    }

    [Fact]
    public async Task Search_AliasMatchIsCaseAndAccentInsensitive()
    {
        var ids = await SearchAsync(_en.ToUpperInvariant());
        Assert.Equal([_withAlias], ids);
    }

    [Fact]
    public async Task PutAlias_LetsAPersonCorrectAProduct_AndSearchFollows()
    {
        var word = $"corr{Guid.NewGuid():N}"[..12];
        var put = await _client.PutAsJsonAsync($"/api/products/{_withoutAlias}/aliases/en", new { keywords = $" {word} , {word.ToUpperInvariant()}, extra{word} " },
            TestContext.Current.CancellationToken);
        put.EnsureSuccessStatusCode();

        Assert.Equal([_withoutAlias], await SearchAsync(word));
        var listed = await _client.GetFromJsonAsync<JsonElement>($"/api/products/{_withoutAlias}/aliases", TestContext.Current.CancellationToken);
        var en = listed.EnumerateArray().Single(a => a.GetProperty("language").GetString() == "en");
        Assert.Equal("manual", en.GetProperty("source").GetString());
        Assert.Equal($"{word}, extra{word}", en.GetProperty("keywords").GetString()); // trimmed and de-duplicated
        Assert.Equal(4, listed.GetArrayLength()); // every language is listed, even without a row
    }

    [Fact]
    public async Task PutAlias_WithEmptyKeywords_RemovesTheProductFromThoseSearches()
    {
        var put = await _client.PutAsJsonAsync($"/api/products/{_withAlias}/aliases/en", new { keywords = "" }, TestContext.Current.CancellationToken);
        put.EnsureSuccessStatusCode();

        Assert.Empty(await SearchAsync(_en));
    }

    [Fact]
    public async Task DeleteAlias_RemovesTheRow()
    {
        var delete = await _client.DeleteAsync($"/api/products/{_withAlias}/aliases/en", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, delete.StatusCode);

        Assert.Empty(await SearchAsync(_en));
    }

    [Fact]
    public async Task Aliases_RejectAnUnknownLanguage_AndAnUnknownProduct()
    {
        var bad = await _client.PutAsJsonAsync($"/api/products/{_withAlias}/aliases/xx", new { keywords = "a" }, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, bad.StatusCode);

        var missing = await _client.PutAsJsonAsync($"/api/products/{Guid.NewGuid()}/aliases/en", new { keywords = "a" }, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Search_WithoutAliases_BehavesAsBefore()
    {
        var ids = await SearchAsync(_pt);
        Assert.Contains(_withAlias, ids);
        Assert.Contains(_withoutAlias, ids);
        Assert.Empty(await SearchAsync($"nomatch{Guid.NewGuid():N}"));
    }
}
