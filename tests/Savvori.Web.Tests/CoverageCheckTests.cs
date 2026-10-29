using Savvori.Shared;
using Savvori.WebApi.Scraping;

namespace Savvori.Web.Tests;

public sealed class CoverageCheckTests
{
    private static ScrapeOutcome O(int? total, int listed, string key = "c") =>
        new() { Kind = OutcomeKind.Category, Key = key, SiteTotal = total, ProductsListed = listed };

    [Fact]
    public void Coverage_Sums_MinOfListedAndTotal_AcrossOutcomes()
    {
        var w = CoverageCheck.Evaluate([O(50, 60), O(40, 10)]);
        Assert.Equal(["Coverage: listed 60 of 90 site products (67%), below 80%; worst: c 25% (10 of 40)"], w);
    }

    [Fact]
    public void Coverage_IgnoresOutcomesWithoutTotal()
    {
        Assert.Empty(CoverageCheck.Evaluate([O(null, 3), O(0, 3)]));
        Assert.Empty(CoverageCheck.Evaluate([O(null, 0), O(10, 10)]));
    }

    [Fact]
    public void NestedListings_CountParentOnly()
    {
        Assert.Empty(CoverageCheck.Evaluate([O(100, 100, "a"), O(60, 0, "a/x"), O(40, 0, "a/y")]));
        var w = CoverageCheck.Evaluate([O(100, 50, "a"), O(60, 0, "a/x"), O(40, 0, "a/y")]);
        Assert.Contains("of 100 site products", Assert.Single(w));
    }

    [Fact]
    public void NamesWorstListings()
    {
        var w = Assert.Single(CoverageCheck.Evaluate([O(100, 10, "p"), O(100, 20, "q"), O(100, 30, "r"), O(100, 40, "s")]));
        Assert.Contains("worst: p 10% (10 of 100), q 20% (20 of 100), r 30% (30 of 100)", w);
        Assert.DoesNotContain("s 40%", w);
    }
}
