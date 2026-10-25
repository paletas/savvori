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
        bool fallback = false,
        CanaryResult? canary = null,
        IReadOnlyList<ScrapedProduct>? products = null) =>
        new(products ?? [], outcomes, warnings ?? [], fallback, canary);

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
    public void DiscoveryFallback_IsDegraded()
    {
        var h = ScrapeHealthEvaluator.Evaluate(Run([Outcome(OutcomeResult.Ok)], fallback: true));
        Assert.Equal(ScrapingJobStatus.Degraded, h.Status);
        Assert.Contains("Discovery fell back to a static category list", h.Reasons);
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
