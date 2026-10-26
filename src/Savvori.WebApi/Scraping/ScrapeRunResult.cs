using Savvori.Shared;

namespace Savvori.WebApi.Scraping;

public record CanaryResult(bool Ok, string Message, int Requests);

public sealed class ScrapeOutcome
{
    public OutcomeKind Kind { get; set; }
    public string Key { get; set; } = "";
    public string Url { get; set; } = "";
    public OutcomeResult Result { get; set; } = OutcomeResult.Ok;
    public int PagesFetched { get; set; }
    public int ProductsFound { get; set; }
    public int TilesSeen { get; set; }
    public int Retries { get; set; }
    public Dictionary<int, int> StatusCounts { get; set; } = new();
    public string? FirstError { get; set; }
}

public record ScrapeRunResult(
    IReadOnlyList<ScrapedProduct> Products,
    IReadOnlyList<ScrapeOutcome> Outcomes,
    IReadOnlyList<string> Warnings,
    bool DiscoveryFallbackUsed = false,
    CanaryResult? Canary = null);
