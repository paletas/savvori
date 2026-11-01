using Savvori.WebApi.Scraping;

namespace Savvori.Web.Tests;

public class PagingGuardTests
{
    private static string[] Ids(string prefix, int n) => Enumerable.Range(0, n).Select(i => $"{prefix}{i}").ToArray();

    [Fact]
    public void Loop_RepeatedPage()
    {
        var g = new PagingGuard("cat-x", 2, null);
        Assert.Equal(PagingVerdict.Continue, g.Observe(["a", "b"], 0, 2));
        Assert.Equal(PagingVerdict.Loop, g.Observe(["a", "b"], 2, 2));
        Assert.Equal("repeated page", g.Reason);
    }

    [Fact]
    public void Loop_OffsetIgnored()
    {
        var g = new PagingGuard("cat-x", 2, null);
        Assert.Equal(PagingVerdict.Continue, g.Observe(["a", "b"], 0, 2));
        Assert.Equal(PagingVerdict.Continue, g.Observe(["c", "d"], 2, 2));
        Assert.Equal(PagingVerdict.Loop, g.Observe(["a", "x"], 4, 2));
        Assert.Equal("offset ignored", g.Reason);
    }

    [Fact]
    public void Loop_NoNewProducts()
    {
        var g = new PagingGuard("cat-x", 2, null);
        Assert.Equal(PagingVerdict.Continue, g.Observe(["a", "b"], 0, 2));
        Assert.Equal(PagingVerdict.Continue, g.Observe(["c", "d"], 2, 2));
        Assert.Equal(PagingVerdict.Loop, g.Observe(["d", "b"], 4, 2));
        Assert.Equal("no new products", g.Reason);
    }

    [Fact]
    public void Loop_PastDeclaredTotal()
    {
        var g = new PagingGuard("cat-x", 2, 4);
        Assert.Equal(PagingVerdict.Continue, g.Observe(["a", "b"], 0, 2));
        Assert.Equal(PagingVerdict.NaturalEnd, g.Observe(["c", "d"], 2, 2));
        Assert.Equal(PagingVerdict.Loop, g.Observe(["e", "f"], 4, 2));
        Assert.Equal("past declared total", g.Reason);
    }

    [Fact]
    public void Loop_PastDeclaredTotal_FirstCallAtTotal()
    {
        var g = new PagingGuard("cat-x", 2, 4);
        Assert.Equal(PagingVerdict.Loop, g.Observe(["a", "b"], 4, 2));
        Assert.Equal("past declared total", g.Reason);
    }

    [Fact]
    public void Loop_PageBound_500()
    {
        var g = new PagingGuard("cat-x", 2, null);
        for (var p = 1; p < 500; p++)
            Assert.Equal(PagingVerdict.Continue, g.Observe([$"p{p}a", $"p{p}b"], (p - 1) * 2, 2));
        Assert.Equal(PagingVerdict.Loop, g.Observe(["p500a", "p500b"], 998, 2));
        Assert.Equal("page bound", g.Reason);
        Assert.Equal(500, g.PageNumber);
    }

    [Fact]
    public void Loop_NoPageBoundWhenTotalDeclared()
    {
        var g = new PagingGuard("cat-x", 2, 10_000);
        for (var p = 1; p <= 600; p++)
            Assert.Equal(PagingVerdict.Continue, g.Observe([$"p{p}a", $"p{p}b"], (p - 1) * 2, 2));
    }

    [Fact]
    public void NaturalEnd_Empty()
    {
        var g = new PagingGuard("cat-x", 2, null);
        Assert.Equal(PagingVerdict.NaturalEnd, g.Observe([], 0, 0));
        Assert.Null(g.Reason);
    }

    [Fact]
    public void NaturalEnd_ShortPage()
    {
        var g = new PagingGuard("cat-x", 3, null);
        Assert.Equal(PagingVerdict.Continue, g.Observe(Ids("a", 3), 0, 3));
        Assert.Equal(PagingVerdict.NaturalEnd, g.Observe(Ids("b", 2), 3, 2));
    }

    [Fact]
    public void Continue_FullFreshPage()
    {
        var g = new PagingGuard("cat-x", 3, null);
        Assert.Equal(PagingVerdict.Continue, g.Observe(Ids("a", 3), 0, 3));
        Assert.Equal(1, g.PageNumber);
    }

    [Fact]
    public void Loop_ReportLoop_WarnsAndSetsIncomplete()
    {
        var (s, _) = BaseHttpScraperBudgetTests.Create();
        var g = new PagingGuard("cat-x", 2, null);
        g.Observe(["a", "b"], 0, 2);
        Assert.Equal(PagingVerdict.Loop, g.Observe(["a", "b"], 2, 2));
        var w = new List<string>();
        s.Loop(g, w);
        Assert.Equal("Loop detected in cat-x at page 2: repeated page; category ended", Assert.Single(w));
        Assert.True(s.Build([], [], w).Incomplete);
    }

    [Fact]
    public void TotalDeclared_ServedFewerThanRequested_WalksToTotal()
    {
        var g = new PagingGuard("c", 48, 100);
        Assert.Equal(PagingVerdict.Continue, g.Observe(Ids("a", 35), 0, 35));
        Assert.Equal(PagingVerdict.Continue, g.Observe(Ids("b", 35), 35, 35));
        Assert.Equal(PagingVerdict.NaturalEnd, g.Observe(Ids("c", 30), 70, 30));
        Assert.StartsWith("reached declared total", g.StopReason);
        Assert.Equal(3, g.PageNumber);
    }

    // All-seen pages over {a,b,c}: never start with "a" (rule 3) and never equal the previous page (rule 2).
    private static string[] Seen(int i) => (i % 2 == 0 ? ["b", "c"] : ["c", "b"]);

    [Fact]
    public void TotalDeclared_AllSeenPagesBelowTotal_AreTolerated()
    {
        var g = new PagingGuard("c", 2, 1000);
        Assert.Equal(PagingVerdict.Continue, g.Observe(["a", "b"], 0, 2));
        Assert.Equal(PagingVerdict.Continue, g.Observe(["c", "d"], 2, 2));
        // snapshot shift: two pages of already-seen ids, then new products
        Assert.Equal(PagingVerdict.Continue, g.Observe(["b", "c"], 4, 2));
        Assert.Equal(PagingVerdict.Continue, g.Observe(["d", "b"], 6, 2));
        Assert.Equal(PagingVerdict.Continue, g.Observe(["e", "f"], 8, 2));
        Assert.Null(g.StopReason);
    }

    [Fact]
    public void TotalDeclared_AllSeenCounterResetsOnNewProducts()
    {
        var g = new PagingGuard("c", 2, 1000);
        g.Observe(["a", "b", "c"], 0, 3);
        for (var round = 0; round < 3; round++)
        {
            for (var i = 0; i < PagingGuard.AllSeenTolerance; i++)
                Assert.Equal(PagingVerdict.Continue, g.Observe(Seen(i), 2 + i, 2));
            Assert.Equal(PagingVerdict.Continue, g.Observe([$"n{round}", $"m{round}"], 100 + round, 2));
        }
    }

    [Fact]
    public void TotalDeclared_EndlessAllSeen_LoopsAfterTolerance()
    {
        var g = new PagingGuard("c", 2, 1000);
        Assert.Equal(PagingVerdict.Continue, g.Observe(["a", "b", "c"], 0, 3));
        for (var i = 0; i < PagingGuard.AllSeenTolerance; i++)
            Assert.Equal(PagingVerdict.Continue, g.Observe(Seen(i), 2 + i, 2));
        Assert.Equal(PagingVerdict.Loop, g.Observe(Seen(PagingGuard.AllSeenTolerance), 20, 2));
        Assert.Equal("no new products", g.Reason);
    }

    [Fact]
    public void TotalDeclared_AllSeenAtOrOverTotal_StillStops()
    {
        var g = new PagingGuard("c", 2, 10);
        g.Observe(["a", "b"], 0, 2);
        // reaches the declared total with nothing new: loop, not tolerated
        Assert.Equal(PagingVerdict.Loop, g.Observe(["b", "a"], 8, 2));
        var over = new PagingGuard("c", 2, 10);
        over.Observe(["a", "b"], 0, 2);
        Assert.Equal(PagingVerdict.Loop, over.Observe(["b", "a"], 12, 2));
    }

    [Fact]
    public void NoTotal_AllSeenPage_StillLoopsImmediately()
    {
        var g = new PagingGuard("c", 2, null);
        g.Observe(["a", "b"], 0, 2);
        g.Observe(["c", "d"], 2, 2);
        Assert.Equal(PagingVerdict.Loop, g.Observe(["b", "c"], 4, 2));
    }

    [Fact]
    public void TotalDeclared_OneUnparseableTile_Continues()
    {
        var g = new PagingGuard("c", 10, 100);
        Assert.Equal(PagingVerdict.Continue, g.Observe(Ids("a", 9), 0, 10));
    }

    [Fact]
    public void NoTotal_ShortRawPage_NaturalEnd()
    {
        var g = new PagingGuard("c", 3, null);
        Assert.Equal(PagingVerdict.Continue, g.Observe(Ids("a", 3), 0, 3));
        Assert.Equal(PagingVerdict.NaturalEnd, g.Observe(Ids("b", 2), 3, 2));
        Assert.StartsWith("short page", g.StopReason);
    }

    [Fact]
    public void NoTotal_FullRawPageWithUnparseable_Continues()
    {
        var g = new PagingGuard("c", 3, null);
        Assert.Equal(PagingVerdict.Continue, g.Observe(Ids("a", 2), 0, 3));
    }

    [Fact]
    public void CounterlessRepeatOnPage2_WithFlag_NaturalEnd()
    {
        var g = new PagingGuard("c", null, null, counterlessRepeatEndsListing: true);
        Assert.Equal(PagingVerdict.Continue, g.Observe(Ids("a", 3), 0, 3));
        Assert.Equal(PagingVerdict.NaturalEnd, g.Observe(Ids("a", 3), 3, 3));
        Assert.Equal("landing page: page 2 repeats page 1, no counter", g.StopReason);
    }

    [Fact]
    public void CountedRepeat_WithFlag_StillLoop()
    {
        var g = new PagingGuard("c", null, 1000, counterlessRepeatEndsListing: true);
        Assert.Equal(PagingVerdict.Continue, g.Observe(Ids("a", 3), 0, 3));
        Assert.Equal(PagingVerdict.Loop, g.Observe(Ids("a", 3), 3, 3));
    }

    [Fact]
    public void EveryStop_SetsStopReason()
    {
        static void Check(PagingGuard g) { Assert.False(string.IsNullOrEmpty(g.StopReason)); }

        var empty = new PagingGuard("c", 2, null);
        empty.Observe([], 0, 0);
        Check(empty);

        var repeated = new PagingGuard("c", 2, null);
        repeated.Observe(["a", "b"], 0, 2);
        Assert.Null(repeated.StopReason);
        repeated.Observe(["a", "b"], 2, 2);
        Check(repeated);

        var ignored = new PagingGuard("c", 2, null);
        ignored.Observe(["a", "b"], 0, 2);
        ignored.Observe(["c", "d"], 2, 2);
        ignored.Observe(["a", "x"], 4, 2);
        Check(ignored);

        var noNew = new PagingGuard("c", 2, null);
        noNew.Observe(["a", "b"], 0, 2);
        noNew.Observe(["c", "d"], 2, 2);
        noNew.Observe(["d", "b"], 4, 2);
        Check(noNew);

        var past = new PagingGuard("c", 2, 4);
        past.Observe(["a", "b"], 4, 2);
        Check(past);

        var shortPage = new PagingGuard("c", 3, null);
        shortPage.Observe(Ids("a", 2), 0, 2);
        Check(shortPage);

        var reached = new PagingGuard("c", 2, 2);
        reached.Observe(["a", "b"], 0, 2);
        Check(reached);

        var bound = new PagingGuard("c", 2, null, 2);
        bound.Observe(["a", "b"], 0, 2);
        Assert.Null(bound.StopReason);
        bound.Observe(["c", "d"], 2, 2);
        Check(bound);
    }
}
