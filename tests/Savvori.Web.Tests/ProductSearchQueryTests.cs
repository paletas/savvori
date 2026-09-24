using Savvori.WebApi.Services;

namespace Savvori.Web.Tests;

public class ProductSearchQueryTests
{
    [Fact]
    public void Glossary_LoadsWithoutATermInTwoGroups_AndEveryGroupHasEquivalents()
    {
        // Touching the glossary builds it; a term in two groups throws.
        var groups = SearchGlossary.FoldedGroups();
        Assert.NotEmpty(groups);
        Assert.All(groups, g => Assert.True(g.Length >= 2, $"Group [{string.Join(", ", g)}] has no equivalent term."));
    }

    [Fact]
    public void Glossary_IsSymmetric_EveryMemberFindsTheSameGroup()
    {
        foreach (var group in SearchGlossary.FoldedGroups())
            foreach (var term in group)
                Assert.Same(group, SearchGlossary.Lookup(term));
    }

    [Theory]
    [InlineData("rice", "arroz")]
    [InlineData("ARROZ", "rice")]
    [InlineData("Açúcar", "sugar")]
    [InlineData("sugar", "acucar")]
    [InlineData("ovos", "eggs")]
    public void Parse_ExpandsAWordToItsEquivalentsInBothLanguages(string typed, string equivalent)
    {
        var concept = Assert.Single(ProductSearchQuery.Parse(typed));
        Assert.Contains(equivalent, concept.Alternatives);
        Assert.True(concept.InGlossary);
    }

    [Fact]
    public void Parse_TakesTheLongestGlossaryPhrase_SoOliveOilIsOneConcept()
    {
        var concept = Assert.Single(ProductSearchQuery.Parse("Olive Oil"));
        Assert.Contains("azeite", concept.Alternatives);
    }

    [Fact]
    public void Parse_SplitsWords_AndOnlyExpandsTheOnesInTheGlossary()
    {
        var concepts = ProductSearchQuery.Parse("carolino rice");
        Assert.Equal(2, concepts.Count);
        Assert.Equal(["carolino"], concepts[0].Alternatives);
        Assert.False(concepts[0].InGlossary);
        Assert.Contains("arroz", concepts[1].Alternatives);
    }

    [Fact]
    public void Parse_KeepsAnUnknownWordAsAPlainSubstringTerm()
    {
        var concept = Assert.Single(ProductSearchQuery.Parse("arro"));
        Assert.False(concept.InGlossary);
        Assert.Equal(["arro"], concept.Alternatives);
    }

    [Fact]
    public void Parse_FallsBackToTheRawTextWhenNothingSurvivesFolding_AndIsEmptyForBlank()
    {
        Assert.Equal(["%%"], Assert.Single(ProductSearchQuery.Parse(" %% ")).Alternatives);
        Assert.Empty(ProductSearchQuery.Parse("   "));
        Assert.Empty(ProductSearchQuery.Parse(null));
    }
}
