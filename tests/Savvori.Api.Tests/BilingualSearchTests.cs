using System.Net.Http.Json;
using System.Text.Json;
using Savvori.Api.Tests.Infrastructure;
using Savvori.WebApi.Scraping;

namespace Savvori.Api.Tests;

/// <summary>
/// Search is language-agnostic (English and Portuguese find the same products) and ordered by relevance.
/// The database is shared with other tests, so each product carries a per-run marker and assertions are about
/// set equality and relative order, never about the exact list for a real word.
/// </summary>
public class BilingualSearchTests : IClassFixture<SavvoriWebApiFactory>
{
    private readonly HttpClient _client;
    private readonly string _m = Guid.NewGuid().ToString("N")[..8];
    private readonly Dictionary<string, Guid> _ids = new();

    public BilingualSearchTests(SavvoriWebApiFactory factory)
    {
        _client = factory.CreateClient();
        factory.SeedData(db =>
        {
            foreach (var name in new[]
                     {
                         "Arroz Carolino", "Papa Infantil Sabor Arroz", "Pilaf Rice", "Aptamil Arroz Banana",
                         "Ovos Classe M", "Free Range Eggs", "Novo Produto", "Sal Marinho", "Salada Mista",
                         "Azeite Virgem Extra", "Acucar Branco", "Leite Meio Gordo"
                     })
            {
                var product = TestDataSeeder.CreateTestProduct($"{name} {_m}");
                product.NormalizedName = ProductNormalizer.Normalize(product.Name);
                _ids[name] = product.Id;
                db.Products.Add(product);
            }
        });
    }

    private async Task<(List<Guid> Ids, int Total)> SearchAsync(string term, int page = 1, int pageSize = 100)
    {
        var body = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/products?search={Uri.EscapeDataString(term)}&page={page}&pageSize={pageSize}", TestContext.Current.CancellationToken);
        return (body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList(),
            body.GetProperty("total").GetInt32());
    }

    private async Task<List<Guid>> IdsAsync(string term) => (await SearchAsync(term)).Ids;

    [Theory]
    [InlineData("rice", "arroz")]
    [InlineData("eggs", "ovo")]
    [InlineData("egg", "ovos")]
    [InlineData("olive oil", "azeite")]
    [InlineData("sugar", "açúcar")]
    [InlineData("milk", "leite")]
    public async Task English_AndPortuguese_FindTheSameProducts(string english, string portuguese)
    {
        var en = await IdsAsync(english);
        var pt = await IdsAsync(portuguese);

        Assert.NotEmpty(en);
        Assert.Equal(pt.Order(), en.Order());
    }

    [Fact]
    public async Task Rice_FindsPortugueseNamedProducts_AndArroz_FindsEnglishNamedOnes()
    {
        var rice = await IdsAsync("rice");
        var arroz = await IdsAsync("arroz");

        foreach (var name in new[] { "Arroz Carolino", "Pilaf Rice" })
        {
            Assert.Contains(_ids[name], rice);
            Assert.Contains(_ids[name], arroz);
        }
    }

    [Fact]
    public async Task MixedLanguageQuery_EveryWordMustMatch_InEitherLanguage()
    {
        var ids = await IdsAsync($"carolino rice {_m}");

        Assert.Contains(_ids["Arroz Carolino"], ids);
        Assert.DoesNotContain(_ids["Pilaf Rice"], ids);
    }

    [Fact]
    public async Task GlossaryTerms_MatchWholeWordsOnly()
    {
        Assert.DoesNotContain(_ids["Novo Produto"], await IdsAsync("egg"));
        Assert.DoesNotContain(_ids["Novo Produto"], await IdsAsync("ovo"));

        var salt = await IdsAsync("salt");
        Assert.Contains(_ids["Sal Marinho"], salt);
        Assert.DoesNotContain(_ids["Salada Mista"], salt);
    }

    [Fact]
    public async Task WordsOutsideTheGlossary_StillMatchAsSubstrings_SoPartialTypingWorks()
    {
        Assert.Contains(_ids["Arroz Carolino"], await IdsAsync($"caroli {_m}"));
        Assert.Contains(_ids["Salada Mista"], await IdsAsync($"lada {_m}"));
    }

    [Fact]
    public async Task Search_IsAccentAndCaseInsensitive()
    {
        Assert.Contains(_ids["Acucar Branco"], await IdsAsync("AÇÚCAR"));
        Assert.Contains(_ids["Acucar Branco"], await IdsAsync("Acucar"));
    }

    [Fact]
    public async Task Relevance_NamesStartingWithTheTermComeBeforeNamesThatOnlyContainIt()
    {
        var ids = await IdsAsync("rice");
        var index = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);

        Assert.True(index[_ids["Arroz Carolino"]] < index[_ids["Papa Infantil Sabor Arroz"]]);
        Assert.True(index[_ids["Arroz Carolino"]] < index[_ids["Aptamil Arroz Banana"]]);
        Assert.True(index[_ids["Pilaf Rice"]] > index[_ids["Arroz Carolino"]]);
    }

    [Fact]
    public async Task Paging_IsStable_AndTotalCountsEveryMatch()
    {
        var (all, total) = await SearchAsync("rice");
        var pages = new List<Guid>();
        for (var page = 1; page <= (all.Count + 1) / 2 + 1; page++)
            pages.AddRange((await SearchAsync("rice", page, pageSize: 2)).Ids);

        Assert.Equal(all.Count, total);
        Assert.Equal(all, pages);
        Assert.Equal(pages.Count, pages.Distinct().Count());
    }
}
