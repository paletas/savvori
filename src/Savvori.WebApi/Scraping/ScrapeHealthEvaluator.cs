using Savvori.Shared;

namespace Savvori.WebApi.Scraping;

public record RunHealth(ScrapingJobStatus Status, IReadOnlyList<string> Reasons, IReadOnlyList<string> Warnings);

/// <summary>Pure, binary health verdict from what the scraper saw (statuses, page structure); product count is not an input.</summary>
public static class ScrapeHealthEvaluator
{
    public static RunHealth Evaluate(ScrapeRunResult r)
    {
        var reasons = new List<string>();
        var warnings = new List<string>();

        foreach (var o in r.Outcomes)
        {
            switch (o.Result)
            {
                case OutcomeResult.Failed:
                case OutcomeResult.Redirected:
                case OutcomeResult.Blocked:
                case OutcomeResult.NoTiles:
                case OutcomeResult.MissingFields:
                    var reason = $"{o.Key}: {o.Result} {o.Url}";
                    if (!string.IsNullOrEmpty(o.FirstError)) reason += $" ({o.FirstError})";
                    reasons.Add(reason);
                    break;
                case OutcomeResult.Recovered:
                    warnings.Add($"{o.Key}: recovered after retry");
                    break;
            }
        }

        if (r.DiscoveryFallbackUsed)
            reasons.Add("Discovery fell back to a static category list");

        if (r.Canary is { Ok: false })
            reasons.Add($"Canary: {r.Canary.Message}");

        warnings.AddRange(r.Warnings);

        var status = reasons.Count > 0 ? ScrapingJobStatus.Degraded
            : warnings.Count > 0 ? ScrapingJobStatus.CompletedWithWarnings
            : ScrapingJobStatus.Completed;

        return new RunHealth(status, reasons, warnings);
    }
}
