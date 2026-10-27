using Savvori.WebApi.Scraping;

namespace Savvori.Web.Tests;

public class PagingGuardTests
{
    private static string[] Ids(string prefix, int n) => Enumerable.Range(0, n).Select(i => $"{prefix}{i}").ToArray();

    [Fact]
    public void Loop_RepeatedPage()
    {
        var g = new PagingGuard("cat-x", 2, null);
        Assert.Equal(PagingVerdict.Continue, g.Observe(["a", "b"], 0));
        Assert.Equal(PagingVerdict.Loop, g.Observe(["a", "b"], 2));
        Assert.Equal("repeated page", g.Reason);
    }

    [Fact]
    public void Loop_OffsetIgnored()
    {
        var g = new PagingGuard("cat-x", 2, null);
        Assert.Equal(PagingVerdict.Continue, g.Observe(["a", "b"], 0));
        Assert.Equal(PagingVerdict.Continue, g.Observe(["c", "d"], 2));
        Assert.Equal(PagingVerdict.Loop, g.Observe(["a", "x"], 4));
        Assert.Equal("offset ignored", g.Reason);
    }

    [Fact]
    public void Loop_NoNewProducts()
    {
        var g = new PagingGuard("cat-x", 2, null);
        Assert.Equal(PagingVerdict.Continue, g.Observe(["a", "b"], 0));
        Assert.Equal(PagingVerdict.Continue, g.Observe(["c", "d"], 2));
        Assert.Equal(PagingVerdict.Loop, g.Observe(["d", "b"], 4));
        Assert.Equal("no new products", g.Reason);
    }

    [Fact]
    public void Loop_PastDeclaredTotal()
    {
        var g = new PagingGuard("cat-x", 2, 4);
        Assert.Equal(PagingVerdict.Continue, g.Observe(["a", "b"], 2));
        Assert.Equal(PagingVerdict.Loop, g.Observe(["c", "d"], 4));
        Assert.Equal("past declared total", g.Reason);
    }

    [Fact]
    public void Loop_PageBound_500()
    {
        var g = new PagingGuard("cat-x", 2, null);
        for (var p = 1; p < 500; p++)
            Assert.Equal(PagingVerdict.Continue, g.Observe([$"p{p}a", $"p{p}b"], (p - 1) * 2));
        Assert.Equal(PagingVerdict.Loop, g.Observe(["p500a", "p500b"], 998));
        Assert.Equal("page bound", g.Reason);
        Assert.Equal(500, g.PageNumber);
    }

    [Fact]
    public void Loop_NoPageBoundWhenTotalDeclared()
    {
        var g = new PagingGuard("cat-x", 2, 10_000);
        for (var p = 1; p <= 600; p++)
            Assert.Equal(PagingVerdict.Continue, g.Observe([$"p{p}a", $"p{p}b"], (p - 1) * 2));
    }

    [Fact]
    public void NaturalEnd_Empty()
    {
        var g = new PagingGuard("cat-x", 2, null);
        Assert.Equal(PagingVerdict.NaturalEnd, g.Observe([], 0));
        Assert.Null(g.Reason);
    }

    [Fact]
    public void NaturalEnd_ShortPage()
    {
        var g = new PagingGuard("cat-x", 3, null);
        Assert.Equal(PagingVerdict.Continue, g.Observe(Ids("a", 3), 0));
        Assert.Equal(PagingVerdict.NaturalEnd, g.Observe(Ids("b", 2), 3));
    }

    [Fact]
    public void Continue_FullFreshPage()
    {
        var g = new PagingGuard("cat-x", 3, null);
        Assert.Equal(PagingVerdict.Continue, g.Observe(Ids("a", 3), 0));
        Assert.Equal(1, g.PageNumber);
    }

    [Fact]
    public void Loop_ReportLoop_WarnsAndSetsIncomplete()
    {
        var (s, _) = BaseHttpScraperBudgetTests.Create();
        var g = new PagingGuard("cat-x", 2, null);
        g.Observe(["a", "b"], 0);
        Assert.Equal(PagingVerdict.Loop, g.Observe(["a", "b"], 2));
        var w = new List<string>();
        s.Loop(g, w);
        Assert.Equal("Loop detected in cat-x at page 2: repeated page; category ended", Assert.Single(w));
        Assert.True(s.Build([], [], w).Incomplete);
    }
}
