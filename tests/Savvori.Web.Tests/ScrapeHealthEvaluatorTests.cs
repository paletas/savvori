using Savvori.Shared;
using Savvori.WebApi.Scraping;

namespace Savvori.Web.Tests;

public sealed class ScrapeHealthEvaluatorTests
{
    private static ScrapeOutcome Outcome(OutcomeResult result, string key = "leite", string? error = null) => new()
    {
        Kind = OutcomeKind.Category,
        Key = key,
        Url = $"https://example.test/{key}",
        Result = result,
        FirstError = error,
    };

    private static ScrapeRunResult Run(
        IReadOnlyList<ScrapeOutcome> outcomes,
        IReadOnlyList<string>? warnings = null,
        CanaryResult? canary = null,
        IReadOnlyList<ScrapedProduct>? products = null,
        bool incomplete = false) =>
        new(products ?? [], outcomes, warnings ?? [], canary, incomplete);

    private static ScrapeOutcome Covered(int siteTotal, int listed) =>
        new() { Kind = OutcomeKind.Category, Key = "c", Url = "https://e/c", Result = OutcomeResult.Ok, SiteTotal = siteTotal, ProductsListed = listed };

    [Fact]
    public void ErrorStatus_IsDegraded()
    {
        var h = ScrapeHealthEvaluator.Evaluate(Run([Outcome(OutcomeResult.Ok, "a"), Outcome(OutcomeResult.Failed, "b", "HTTP 500")]));
        Assert.Equal(ScrapingJobStatus.Degraded, h.Status);
        Assert.Single(h.Reasons);
        Assert.Equal("b: Failed https://example.test/b (HTTP 500)", h.Reasons[0]);
    }

    [Fact]
    public void Redirect_IsDegraded() =>
        Assert.Equal(ScrapingJobStatus.Degraded, ScrapeHealthEvaluator.Evaluate(Run([Outcome(OutcomeResult.Redirected)])).Status);

    [Fact]
    public void BotChallenge_IsDegraded() =>
        Assert.Equal(ScrapingJobStatus.Degraded, ScrapeHealthEvaluator.Evaluate(Run([Outcome(OutcomeResult.Blocked)])).Status);

    [Fact]
    public void NoTiles_IsDegraded() =>
        Assert.Equal(ScrapingJobStatus.Degraded, ScrapeHealthEvaluator.Evaluate(Run([Outcome(OutcomeResult.NoTiles)])).Status);

    [Fact]
    public void TilesMissingFields_IsDegraded() =>
        Assert.Equal(ScrapingJobStatus.Degraded, ScrapeHealthEvaluator.Evaluate(Run([Outcome(OutcomeResult.MissingFields)])).Status);

    [Fact]
    public void DiscoveryFailed_IsFailed()
    {
        var o = new ScrapeOutcome { Kind = OutcomeKind.Discovery, Key = "discovery", Url = "https://e/s.xml", Result = OutcomeResult.Failed, FirstError = "HTTP 500" };
        var h = ScrapeHealthEvaluator.Evaluate(Run([o]));
        Assert.Equal(ScrapingJobStatus.Failed, h.Status);
        Assert.Equal(["Discovery failed: https://e/s.xml (HTTP 500)"], h.Reasons);
    }

    [Fact]
    public void Coverage_Below80_Warns()
    {
        var h = ScrapeHealthEvaluator.Evaluate(Run([Covered(100, 79)]));
        Assert.Equal(ScrapingJobStatus.CompletedWithWarnings, h.Status);
        Assert.Contains("Coverage: listed 79 of 100 site products (79%), below 80%", h.Warnings);
    }

    [Fact]
    public void Coverage_At80_NoWarning()
    {
        var h = ScrapeHealthEvaluator.Evaluate(Run([Covered(100, 80)]));
        Assert.Equal(ScrapingJobStatus.Completed, h.Status);
    }

    [Fact]
    public void Coverage_NoTotal_NoWarning()
    {
        var o = Outcome(OutcomeResult.Ok);
        o.ProductsListed = 5;
        Assert.Equal(ScrapingJobStatus.Completed, ScrapeHealthEvaluator.Evaluate(Run([o])).Status);
    }

    [Theory]
    [InlineData(ScrapingJobStatus.Completed, false, true)]
    [InlineData(ScrapingJobStatus.CompletedWithWarnings, true, false)]
    [InlineData(ScrapingJobStatus.Degraded, false, false)]
    [InlineData(ScrapingJobStatus.Failed, false, false)]
    public void StaleMarkingAllowed_FollowsStatusAndIncomplete(ScrapingJobStatus status, bool incomplete, bool expected)
    {
        var r = Run([], incomplete: incomplete);
        Assert.Equal(expected, ScrapeHealthEvaluator.StaleMarkingAllowed(r, new RunHealth(status, [], [])));
    }

    [Fact]
    public void CanaryFail_IsDegraded()
    {
        var h = ScrapeHealthEvaluator.Evaluate(Run([Outcome(OutcomeResult.Ok)], canary: new CanaryResult(false, "no tiles", 1)));
        Assert.Equal(ScrapingJobStatus.Degraded, h.Status);
        Assert.Contains("Canary: no tiles", h.Reasons);
    }

    [Fact]
    public void Recovered_IsWarning()
    {
        var h = ScrapeHealthEvaluator.Evaluate(Run([Outcome(OutcomeResult.Recovered, "leite")]));
        Assert.Equal(ScrapingJobStatus.CompletedWithWarnings, h.Status);
        Assert.Empty(h.Reasons);
        Assert.Contains("leite: recovered after retry", h.Warnings);
    }

    [Fact]
    public void AllOk_IsCompleted()
    {
        var h = ScrapeHealthEvaluator.Evaluate(Run([Outcome(OutcomeResult.Ok, "a"), Outcome(OutcomeResult.Ok, "b")]));
        Assert.Equal(ScrapingJobStatus.Completed, h.Status);
        Assert.Empty(h.Reasons);
        Assert.Empty(h.Warnings);
    }

    [Fact]
    public void LowCountOnly_IsCompleted()
    {
        var outcomes = Enumerable.Range(0, 20).Select(i => Outcome(OutcomeResult.Ok, $"c{i}")).ToList();
        var product = new ScrapedProduct("Leite", null, null, 1m, null, null, "x-1", null, null, false, null, ProductUnit.L, 1m);
        var h = ScrapeHealthEvaluator.Evaluate(Run(outcomes, products: [product]));
        Assert.Equal(ScrapingJobStatus.Completed, h.Status);
    }
}
