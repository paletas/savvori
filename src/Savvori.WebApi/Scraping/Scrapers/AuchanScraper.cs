using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Savvori.Shared;

namespace Savvori.WebApi.Scraping.Scrapers;

/// <summary>
/// Scraper for Auchan Portugal (auchan.pt).
/// Uses the SFCC platform — products are in .product-tile elements with data-gtm JSON.
/// URL: https://www.auchan.pt/pt/pesquisa?cgid={cgid}&amp;start={N} (24 products per page); there is no keyword list.
/// The default cgid "root" lists the whole assortment; the site total comes from the "N de TOTAL resultados" counter.
/// No sz, srule, pmin, prefn or search-query parameters (robots.txt disallows them); paged via <see cref="PagingGuard"/>.
/// </summary>
public sealed partial class AuchanScraper : BaseHttpScraper
{
    public override string StoreChainSlug => "auchan";

    private const string BaseUrl = "https://www.auchan.pt";
    private const string SearchBase = "https://www.auchan.pt/pt/pesquisa";
    private const string RootCgid = "root";
    private const int PageSize = 24;

    public AuchanScraper(
        IHttpClientFactory httpClientFactory,
        ILogger<AuchanScraper> logger)
        : base(httpClientFactory, logger, "auchan")
    {
        RequestCeiling = 7200;
    }

    public override async Task<ScrapeRunResult> ScrapeProductsAsync(
        string? category = null, CancellationToken ct = default)
    {
        BeginRun();
        var warnings = new List<string>();
        string[] cgids = [category ?? RootCgid];
        var seen = new HashSet<string>();
        var products = new List<ScrapedProduct>();
        var outcomes = new List<ScrapeOutcome>();

        foreach (var cgid in cgids)
        {
            if (Halted) break;
            ct.ThrowIfCancellationRequested();
            await ScrapeSearch(cgid, seen, products, outcomes, warnings, ct);
        }

        await ReattemptAsync(outcomes, async old =>
        {
            var tmp = new List<ScrapeOutcome>();
            await ScrapeSearch(old.Key, seen, products, tmp, warnings, ct);
            return tmp[0];
        }, ct);

        Logger.LogInformation("Auchan: scraped {Count} distinct products", products.Count);
        return BuildResult(products, outcomes, warnings);
    }

    public override Task<CanaryResult> ProbeAsync(CancellationToken ct = default)
        => ProbeHtmlAsync($"{SearchBase}?cgid={RootCgid}&start=0", d =>
        {
            var t = d.QuerySelectorAll(".product-tile").ToList();
            return (t.Count, t.Count(x => ParseTile(x) is not null));
        }, ct);

    public override Task<IReadOnlyList<ScrapedStoreLocation>> ScrapeStoreLocationsAsync(
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ScrapedStoreLocation>>([]);
    }

    private async Task ScrapeSearch(
        string cgid,
        HashSet<string> seen,
        List<ScrapedProduct> products,
        List<ScrapeOutcome> outcomes,
        List<string> warnings,
        CancellationToken ct)
    {
        var outcome = new ScrapeOutcome
        {
            Kind = OutcomeKind.Term,
            Key = cgid,
            Url = $"{SearchBase}?cgid={Uri.EscapeDataString(cgid)}&start=0"
        };
        outcomes.Add(outcome);
        PagingGuard? guard = null;

        for (var offset = 0; ; offset += PageSize)
        {
            if (Halted) { outcome.StopReason ??= $"run halted: {HaltReason}"; return; }

            // Auchan ignores ?page=N (always returns the first page); paging is via ?start=N
            var url = $"{SearchBase}?cgid={Uri.EscapeDataString(cgid)}&start={offset}";
            var document = await GetHtmlAsync(url, outcome, ct);
            if (document is null)
            {
                Logger.LogError("Auchan: failed to fetch cgid='{Cgid}' start={Offset}: {Err}",
                    cgid, offset, outcome.FirstError);
                return;
            }

            var tiles = document.QuerySelectorAll(".product-tile").ToList();
            var parsed = tiles.Select(ParseTile).Where(p => p is not null).Select(p => p!).ToList();
            if (offset == 0)
            {
                MarkFirstPage(outcome, tiles.Count, parsed.Count);
                outcome.SiteTotal = ParseTotal(document);
            }
            outcome.ProductsListed += parsed.Count;

            foreach (var product in parsed)
            {
                if (seen.Add(product.ExternalId))
                {
                    products.Add(product);
                    outcome.ProductsFound++;
                }
            }

            guard ??= new PagingGuard(outcome.Key, PageSize, outcome.SiteTotal);
            var verdict = guard.Observe(parsed.Select(p => p.ExternalId).ToList(), offset, tiles.Count);
            outcome.StopReason = guard.StopReason;
            switch (verdict)
            {
                case PagingVerdict.Loop:
                    ReportLoop(guard, warnings);
                    return;
                case PagingVerdict.NaturalEnd:
                    return;
            }
        }
    }

    private static ScrapedProduct? ParseTile(IElement tile)
    {
        var pid = tile.GetAttribute("data-pid");
        if (string.IsNullOrEmpty(pid)) return null;

        var gtmJson = tile.GetAttribute("data-gtm");
        if (string.IsNullOrEmpty(gtmJson)) return null;

        JsonElement gtm;
        try
        {
            gtm = JsonSerializer.Deserialize<JsonElement>(gtmJson);
        }
        catch
        {
            return null;
        }

        var name = gtm.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
        var brand = gtm.TryGetProperty("brand", out var brandEl) ? brandEl.GetString() : null;
        var categoryPath = gtm.TryGetProperty("category", out var catEl) ? catEl.GetString() : null;
        var priceStr = gtm.TryGetProperty("price", out var priceEl) ? priceEl.GetString() : null;
        var isPromoStr = gtm.TryGetProperty("dimension7", out var d7El) ? d7El.GetString() : null;

        if (string.IsNullOrEmpty(name)) return null;

        decimal.TryParse(priceStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var price);
        if (price <= 0) return null;

        var category = categoryPath?.Split('/').LastOrDefault();
        var isPromo = string.Equals(isPromoStr, "Yes", StringComparison.OrdinalIgnoreCase);

        // Unit price from .auc-measures--price-per-unit: "0.86 €/Lt"
        var unitPriceText = tile.QuerySelector(".auc-measures--price-per-unit")?.TextContent;
        var unitPrice = ParseUnitPrice(unitPriceText);

        // If dimension7 was not "Yes", check for promo element
        if (!isPromo)
            isPromo = tile.QuerySelector(".auc-price__promotion") is not null;

        // Image
        var imgEl = tile.QuerySelector("img[src]");
        var imageUrl = imgEl?.GetAttribute("src");

        // Product URL from data-urls
        var urlsJson = tile.GetAttribute("data-urls");
        string? sourceUrl = null;
        if (!string.IsNullOrEmpty(urlsJson))
        {
            try
            {
                var urls = JsonSerializer.Deserialize<JsonElement>(urlsJson);
                var relUrl = urls.TryGetProperty("absoluteProductUrl", out var absEl) ? absEl.GetString()
                    : urls.TryGetProperty("productUrl", out var relEl) ? relEl.GetString() : null;
                sourceUrl = relUrl is not null
                    ? (relUrl.StartsWith("http") ? relUrl : $"{BaseUrl}{relUrl}")
                    : null;
            }
            catch { /* ignore */ }
        }

        var sizeUnit = ProductNormalizer.ExtractSizeAndUnit(name)
            ?? ProductNormalizer.ExtractSizeAndUnit(tile.TextContent ?? string.Empty, allowPackCount: false);

        return new ScrapedProduct(
            Name: name,
            Brand: brand,
            Category: category,
            Price: price,
            UnitPrice: unitPrice,
            EAN: null,
            ExternalId: pid,
            ImageUrl: imageUrl,
            SourceUrl: sourceUrl,
            IsPromotion: isPromo,
            PromotionDescription: isPromo ? "Promoção" : null,
            Unit: sizeUnit?.Unit ?? ProductUnit.Unit,
            SizeValue: sizeUnit?.SizeValue
        );
    }

    private static decimal? ParseUnitPrice(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // "0.86 €/Lt" → 0.86
        var part = text.Split('€')[0].Trim().Replace(",", ".").Replace(" ", "");
        return decimal.TryParse(part, NumberStyles.Any, CultureInfo.InvariantCulture, out var val) && val > 0
            ? val : null;
    }

    private static int? ParseTotal(IDocument doc)
    {
        var text = doc.QuerySelector(".auc-js-search-results-count")?.TextContent;
        var m = TotalRegex().Match(text ?? "");
        if (!m.Success) return null;
        var digits = m.Groups[1].Value.Replace(",", "").Replace(".", "");
        return int.TryParse(digits, out var n) && n > 0 ? n : null;
    }

    [GeneratedRegex(@"de\s+([\d.,]+)\s+resultados", RegexOptions.IgnoreCase)]
    private static partial Regex TotalRegex();
}
