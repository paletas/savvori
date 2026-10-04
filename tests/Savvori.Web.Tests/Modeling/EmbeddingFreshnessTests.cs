using Savvori.WebApi.Modeling;

namespace Savvori.Web.Tests.Modeling;

public sealed class EmbeddingFreshnessTests
{
    private static EmbeddingMetadata Meta(
        string model = "bge-m3", string digest = "d1", int dim = 1024, string text = "mimosa leite 1l") =>
        new(model, digest, dim, EmbeddingFreshness.HashText(text));

    [Fact]
    public void IdenticalMetadata_IsFresh() =>
        Assert.False(EmbeddingFreshness.IsStale(Meta(), Meta()));

    [Fact]
    public void ChangedModelName_IsStale() =>
        Assert.True(EmbeddingFreshness.IsStale(Meta(), Meta(model: "other-model")));

    [Fact]
    public void ChangedModelDigest_IsStale_EvenWithSameName() =>
        Assert.True(EmbeddingFreshness.IsStale(Meta(), Meta(digest: "d2")));

    [Fact]
    public void ChangedDimension_IsStale() =>
        Assert.True(EmbeddingFreshness.IsStale(Meta(), Meta(dim: 768)));

    [Fact]
    public void ChangedInputText_IsStale() =>
        Assert.True(EmbeddingFreshness.IsStale(Meta(), Meta(text: "mimosa leite 500ml")));

    [Fact]
    public void HashText_IsStableAndTextSensitive()
    {
        Assert.Equal(EmbeddingFreshness.HashText("a"), EmbeddingFreshness.HashText("a"));
        Assert.NotEqual(EmbeddingFreshness.HashText("a"), EmbeddingFreshness.HashText("b"));
    }

    [Theory]
    [InlineData("Mimosa", "Leite Meio Gordo 1L", "mimosa leite meio gordo 1l")]
    [InlineData("Mimosa", "Leite Mimosa Meio Gordo", "leite mimosa meio gordo")] // brand already in the name
    [InlineData(null, "Leite Meio Gordo", "leite meio gordo")]
    [InlineData("  ", "Leite Meio Gordo", "leite meio gordo")]
    public void BuildInputText_LowerCasesAndOmitsBrandAlreadyInName(string? brand, string name, string expected) =>
        Assert.Equal(expected, EmbeddingFreshness.BuildInputText(brand, name));

    [Theory]
    // the store category is appended so the embedder gets the disambiguating signal the name alone lacks
    [InlineData("Continente", "Tentáculos de Polvo Congelados", "Congelados", "continente tentáculos de polvo congelados congelados")]
    [InlineData(null, "Leite Meio Gordo", null, "leite meio gordo")]
    [InlineData(null, "Leite Meio Gordo", "  ", "leite meio gordo")]
    public void BuildInputText_AppendsStoreCategoryWhenPresent(string? brand, string name, string? category, string expected) =>
        Assert.Equal(expected, EmbeddingFreshness.BuildInputText(brand, name, category));
}
