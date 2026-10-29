namespace Savvori.WebApi.Scraping;

/// <summary>Compares products listed against the totals the site reports; a shortfall is a warning.</summary>
public static class CoverageCheck
{
    public const double Threshold = 0.80;

    public static IReadOnlyList<string> Evaluate(IReadOnlyList<ScrapeOutcome> outcomes)
    {
        var withTotal = outcomes.Where(o => o.SiteTotal > 0).ToList();

        static string Norm(string key)
        {
            if (key.EndsWith("/todos")) key = key[..^6];
            return key.TrimEnd('/');
        }

        // A listing nested under another listing (a/x under a) is already counted by its parent.
        var kept = withTotal
            .Where(o => !withTotal.Any(other => !ReferenceEquals(other, o) && Norm(o.Key).StartsWith(Norm(other.Key) + "/")))
            .ToList();
        if (kept.Count == 0) return [];

        var listed = kept.Sum(o => Math.Min(o.ProductsListed, o.SiteTotal!.Value));
        var total = kept.Sum(o => o.SiteTotal!.Value);
        if ((double)listed / total >= Threshold) return [];

        var text = $"Coverage: listed {listed} of {total} site products ({(int)Math.Round(100.0 * listed / total)}%), below 80%";
        var worst = kept
            .Select(o => (o.Key, L: Math.Min(o.ProductsListed, o.SiteTotal!.Value), T: o.SiteTotal!.Value))
            .Where(x => (double)x.L / x.T < Threshold)
            .OrderBy(x => (double)x.L / x.T)
            .Take(3)
            .Select(x => $"{Norm(x.Key)} {(int)Math.Round(100.0 * x.L / x.T)}% ({x.L} of {x.T})")
            .ToList();
        if (worst.Count > 0) text += "; worst: " + string.Join(", ", worst);
        return [text];
    }
}
