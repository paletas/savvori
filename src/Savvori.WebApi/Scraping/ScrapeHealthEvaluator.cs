using Savvori.Shared;

namespace Savvori.WebApi.Scraping;

public record RunHealth(ScrapingJobStatus Status, IReadOnlyList<string> Reasons, IReadOnlyList<string> Warnings);

/// <summary>Pure, binary health verdict from what the scraper saw (statuses, page structure); product count is not an input.</summary>
public static class ScrapeHealthEvaluator
{
    /// <summary>Whole run: one trip anywhere (non-clean status or Incomplete) blocks all stale marking.</summary>
    public static bool StaleMarkingAllowed(ScrapeRunResult r, RunHealth h) =>
        (h.Status == ScrapingJobStatus.Completed || h.Status == ScrapingJobStatus.CompletedWithWarnings) && !r.Incomplete;

    public static RunHealth Evaluate(ScrapeRunResult r)
    {
        var d = r.Outcomes.FirstOrDefault(o => o.Kind == OutcomeKind.Discovery && o.Result == OutcomeResult.Failed);
        if (d is not null)
        {
            var why = $"Discovery failed: {d.Url}";
            if (!string.IsNullOrEmpty(d.FirstError)) why += $" ({d.FirstError})";
            return new RunHealth(ScrapingJobStatus.Failed, [why], [.. r.Warnings]);
        }

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

        if (r.Outcomes.Count > 0 && r.Outcomes.All(o => o.ProductsFound == 0))
            reasons.Add("no listing returned products");

        if (r.Canary is { Ok: false })
            reasons.Add($"Canary: {r.Canary.Message}");

        warnings.AddRange(r.Warnings);
        warnings.AddRange(CoverageCheck.Evaluate(r.Outcomes));

        var status = reasons.Count > 0 ? ScrapingJobStatus.Degraded
            : warnings.Count > 0 ? ScrapingJobStatus.CompletedWithWarnings
            : ScrapingJobStatus.Completed;

        return new RunHealth(status, reasons, warnings);
    }

    /// <summary>Bounds the persisted reason text: first <paramref name="max"/> reasons, a "and N more" tail, hard-capped at <paramref name="maxChars"/>.</summary>
    public static string Summarize(IReadOnlyList<string> reasons, int max = 10, int maxChars = 1000)
    {
        if (reasons.Count == 0) return "";
        var s = string.Join("; ", reasons.Take(max));
        if (reasons.Count > max) s += $"; and {reasons.Count - max} more";
        return s.Length > maxChars ? s[..maxChars] : s;
    }
}
