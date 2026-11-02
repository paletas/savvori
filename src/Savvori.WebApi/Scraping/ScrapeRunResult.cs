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
    /// <summary>Product count the site itself reports for this scope, when known.</summary>
    public int? SiteTotal { get; set; }
    /// <summary>All valid tiles listed, including duplicates; not persisted.</summary>
    public int ProductsListed { get; set; }
    public Dictionary<int, int> StatusCounts { get; set; } = new();
    public string? FirstError { get; set; }

    /// <summary>Why this listing stopped: natural end, loop, bound, block or failure.</summary>
    public string? StopReason { get; set; }
}

public record ScrapeRunResult(
    IReadOnlyList<ScrapedProduct> Products,
    IReadOnlyList<ScrapeOutcome> Outcomes,
    IReadOnlyList<string> Warnings,
    CanaryResult? Canary = null,
    bool Incomplete = false);
