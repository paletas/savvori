namespace Savvori.WebApi.Scraping;

/// <summary>Compares products listed against the totals the site reports; a shortfall is a warning.</summary>
public static class CoverageCheck
{
    public const double Threshold = 0.80;

    public static IReadOnlyList<string> Evaluate(IReadOnlyList<ScrapeOutcome> outcomes)
    {
        var withTotal = outcomes.Where(o => o.SiteTotal > 0).ToList();
        if (withTotal.Count == 0) return [];

        var listed = withTotal.Sum(o => Math.Min(o.ProductsListed, o.SiteTotal!.Value));
        var total = withTotal.Sum(o => o.SiteTotal!.Value);
        if ((double)listed / total < Threshold)
            return [$"Coverage: listed {listed} of {total} site products ({(int)Math.Round(100.0 * listed / total)}%), below 80%"];
        return [];
    }
}
