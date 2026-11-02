namespace Savvori.WebApi.Scraping;

/// <summary>Compares products listed against the totals the site reports; a shortfall is a warning.</summary>
public static class CoverageCheck
{
    public const double Threshold = 0.80;

    /// <summary>Stop reason prefix of a listing ended because it only yielded products already seen.</summary>
    public const string StagnationReason = "no new products for";

    public static IReadOnlyList<string> Evaluate(IReadOnlyList<ScrapeOutcome> outcomes)
    {
        // Listings that stagnated (only repeats of products listed elsewhere, typically catalog-wide search or
        // campaign views declaring the whole catalog as their total) say nothing about missing products.
        var withTotal = outcomes
            .Where(o => o.SiteTotal > 0 && o.StopReason?.StartsWith(StagnationReason, StringComparison.Ordinal) != true)
            .ToList();

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
