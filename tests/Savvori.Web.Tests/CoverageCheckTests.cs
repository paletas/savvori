using Savvori.Shared;
using Savvori.WebApi.Scraping;

namespace Savvori.Web.Tests;

public sealed class CoverageCheckTests
{
    private static ScrapeOutcome O(int? total, int listed) =>
        new() { Kind = OutcomeKind.Category, Key = "c", SiteTotal = total, ProductsListed = listed };

    [Fact]
    public void Coverage_Sums_MinOfListedAndTotal_AcrossOutcomes()
    {
        var w = CoverageCheck.Evaluate([O(50, 60), O(40, 10)]);
        Assert.Equal(["Coverage: listed 60 of 90 site products (67%), below 80%"], w);
    }

    [Fact]
    public void Coverage_IgnoresOutcomesWithoutTotal()
    {
        Assert.Empty(CoverageCheck.Evaluate([O(null, 3), O(0, 3)]));
        Assert.Empty(CoverageCheck.Evaluate([O(null, 0), O(10, 10)]));
    }
}
