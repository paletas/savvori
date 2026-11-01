using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AngleSharp.Dom;
using Savvori.Shared;

namespace Savvori.WebApi.Scraping.Scrapers;

public sealed partial class ContinenteScraper : BaseHttpScraper
{
    public override string StoreChainSlug => "continente";

    private const string BaseUrl = "https://www.continente.pt";
    private const string StoreFinderUrl =
        "https://www.continente.pt/on/demandware.store/Sites-continente-Site/default/Stores-FindStores";
    private const int PageSize = 48;

    // The site answers sz=48 with up to ~47 tiles but pages in steps of 35 (start=35 is the next page, start=47 repeats
    // it). The real step is read from the first-page counter ("35 de 1080"); this is the fallback when there is none.
    private const int DefaultPageStep = 35;

    // A category that adds no product new to the whole run for this many consecutive pages ends early. It stops
    // catalogue-wide listings (presentes/jantar-romantico serves "35 de 69358") from eating the wall clock, while a
    // category still yielding new products (e.g. pesquisa) never trips it because any new product resets the count.
    private const int MaxPagesWithoutNewProducts = 10;

    // Geographic sweep centres — 300 km radius covers all of mainland Portugal from two points.
    private static readonly (double Lat, double Lng)[] SweepCentres =
    [
        (41.15, -8.61),   // Porto  — covers north + centre
        (37.02, -7.93),   // Faro   — covers Algarve + Alentejo south
    ];

    private const string SitemapIndexUrl = $"{BaseUrl}/sitemap_index.xml";

    // Brands pages repeat products already listed under the departments; destaques is a promotional landing section.
    private static readonly string[] ExcludedTops = ["marcas", "destaques"];

    // Sections scraped first, in this order; every other section follows alphabetically.
    private static readonly string[] FirstTops = ["supermercado", "bebe", "animais", "beleza"];

    // Real leaf category requested by the canary probe.
    private const string ProbeCategory = "supermercado/frutas-e-legumes/frutas/banana";

    public ContinenteScraper(
        IHttpClientFactory httpClientFactory,
        ILogger<ContinenteScraper> logger)
        : base(httpClientFactory, logger, "continente")
    {
        RequestCeiling = 8000;
        WallClockLimit = TimeSpan.FromHours(8);
    }

    public override async Task<ScrapeRunResult> ScrapeProductsAsync(
        string? category = null, CancellationToken ct = default)
    {
        BeginRun();
        var warnings = new List<string>();
        IReadOnlyList<string> categories;
        if (category is not null)
        {
            categories = [category];
        }
        else
        {
            (categories, var discoveryFailure) = await DiscoverCategoryPathsAsync(ct);
            if (discoveryFailure is not null) return DiscoveryFailed(SitemapIndexUrl, discoveryFailure);
        }

        var seen = new HashSet<string>();
        var products = new List<ScrapedProduct>();
        var outcomes = new List<ScrapeOutcome>();

        SetKeysTotal(categories.Count);
        foreach (var cat in categories)
        {
            if (Halted) break;
            ct.ThrowIfCancellationRequested();
            await ScrapeCategory(cat, seen, products, outcomes, warnings, ct);
            ReportKeyDone(cat, products.Count);
        }

        await ReattemptAsync(outcomes, async old =>
        {
            var tmp = new List<ScrapeOutcome>();
            await ScrapeCategory(old.Key, seen, products, tmp, warnings, ct);
            return tmp[0];
        }, ct);

        Logger.LogInformation("Continente: scraped {Count} distinct products across {Cats} categories",
            products.Count, categories.Count);
        return BuildResult(products, outcomes, warnings);
    }

    public override async Task<CanaryResult> ProbeAsync(CancellationToken ct = default)
    {
        var o = new ScrapeOutcome { Url = SitemapIndexUrl };
        var xml = await GetStringAsync(SitemapIndexUrl, o, ct);
        var requests = o.StatusCounts.Values.Sum();
        if (xml is null)
            return new CanaryResult(false, $"{SitemapIndexUrl}: {o.Result} {o.FirstError}".Trim(), requests);

        try
        {
            var index = XDocument.Parse(xml);
            var ns = index.Root?.GetDefaultNamespace() ?? XNamespace.None;
            var hasCategorySitemap = index.Descendants(ns + "loc").Any(e =>
                e.Value.Contains("categor", StringComparison.OrdinalIgnoreCase) &&
                !e.Value.Contains("refinement", StringComparison.OrdinalIgnoreCase));
            if (!hasCategorySitemap)
                return new CanaryResult(false, $"{SitemapIndexUrl}: no category sitemap", requests);
        }
        catch (Exception)
        {
            return new CanaryResult(false, $"{SitemapIndexUrl}: unparseable response", requests);
        }

        var page = await ProbeHtmlAsync(
            $"{BaseUrl}/{ProbeCategory}/?start=0&sz={PageSize}&srule=Continente", d =>
            {
                var t = d.QuerySelectorAll(".product-tile").ToList();
                return (t.Count, t.Count(x => ParseTile(x) is not null));
            }, ct);
        return page with { Requests = page.Requests + requests };
    }

    public override async Task<IReadOnlyList<ScrapedStoreLocation>> ScrapeStoreLocationsAsync(
        CancellationToken ct = default)
    {
        var seen = new HashSet<string>();
        var locations = new List<ScrapedStoreLocation>();

        foreach (var (lat, lng) in SweepCentres)
        {
            ct.ThrowIfCancellationRequested();
            var url = $"{StoreFinderUrl}?lat={lat.ToString(CultureInfo.InvariantCulture)}" +
                      $"&long={lng.ToString(CultureInfo.InvariantCulture)}&radius=300&countryCode=PT&distanceUnit=km";

            try
            {
                var response = await GetJsonAsync<ContinenteStoreFinderResponse>(url, ct);
                if (response?.Stores is null) continue;

                foreach (var store in response.Stores)
                {
                    if (string.IsNullOrEmpty(store.Id) || !seen.Add(store.Id)) continue;
                    if (string.IsNullOrEmpty(store.Name)) continue;

                    locations.Add(new ScrapedStoreLocation(
                        Name: store.Name,
                        Address: store.Address1,
                        PostalCode: store.PostalCode,
                        City: store.City,
                        Latitude: store.Latitude,
                        Longitude: store.Longitude
                    ));
                }

                Logger.LogInformation(
                    "Continente: centre ({Lat},{Lng}) returned {Count} stores (total: {Total})",
                    lat, lng, response.Stores.Count, locations.Count);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Continente: failed to fetch store locations (centre {Lat},{Lng})", lat, lng);
            }

            await Task.Delay(500, ct);
        }

        Logger.LogInformation("Continente: {Count} distinct store locations scraped", locations.Count);
        return locations;
    }

    /// <summary>
    /// Selects the categories to scrape from the sitemap paths: every top-level section except
    /// <c>marcas</c> (brand pages repeat the departments), no allowlist. Per section: the
    /// <c>T/todos</c> page when it is present, else every distinct second-level
    /// <c>T/x</c>, else the section itself. Paths with a <c>ver-todos</c> segment are ignored
    /// (robots.txt disallows them). Order: supermercado, bebe, animais, beleza, then the other
    /// sections alphabetically.
    /// </summary>
    public static IReadOnlyList<string> SelectCategories(IEnumerable<string> sitemapPaths)
    {
        var byTop = new Dictionary<string, List<string[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in sitemapPaths)
        {
            var path = raw.Trim().Trim('/');
            if (path.Length == 0) continue;
            var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length == 0 || segs.Any(x => x.Contains("ver-todos", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (ExcludedTops.Any(t => string.Equals(segs[0], t, StringComparison.OrdinalIgnoreCase))) continue;
            if (!byTop.TryGetValue(segs[0], out var list)) byTop[segs[0]] = list = [];
            list.Add(segs);
        }

        var tops = FirstTops.Where(byTop.ContainsKey)
            .Concat(byTop.Keys
                .Where(k => !FirstTops.Contains(k, StringComparer.OrdinalIgnoreCase))
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase));

        var result = new List<string>();
        foreach (var top in tops)
        {
            var paths = byTop[top];
            var todos = paths.FirstOrDefault(x => x.Length == 2 && x[1].Equals("todos", StringComparison.OrdinalIgnoreCase));
            if (todos is not null)
            {
                result.Add($"{todos[0]}/todos");
                continue;
            }
            var children = paths.Where(x => x.Length >= 2)
                .Select(x => $"{x[0]}/{x[1]}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (children.Count > 0) result.AddRange(children);
            else result.Add(top);
        }
        return result;
    }

    /// <summary>
    /// Discovers category paths from the Continente sitemap.
    /// Fetches the sitemap index, locates the category sitemap (excluding refinement sitemaps),
    /// collects every in-domain category URL (no allowlist) and applies <see cref="SelectCategories"/>
    /// (todos page or second-level categories per section; <c>marcas</c> excluded; supermercado,
    /// bebe, animais, beleza first). On failure returns no paths and a <c>FailureReason</c>
    /// (the run is then reported as failed discovery).
    /// </summary>
    private async Task<(IReadOnlyList<string> Paths, string? FailureReason)> DiscoverCategoryPathsAsync(CancellationToken ct)
    {
        try
        {
            // Step 1: Parse sitemap index to find the category sitemap URL(s).
            var indexXml = await GetStringAsync(SitemapIndexUrl, ct);
            var index = XDocument.Parse(indexXml);
            var ns = index.Root?.GetDefaultNamespace() ?? XNamespace.None;

            var categorySitemapUrls = index.Descendants(ns + "loc")
                .Select(e => e.Value.Trim())
                .Where(u =>
                    u.Contains("categor", StringComparison.OrdinalIgnoreCase) &&
                    !u.Contains("refinement", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (categorySitemapUrls.Count == 0)
            {
                Logger.LogWarning("Continente: no category sitemap found in {Url}, discovery failed", SitemapIndexUrl);
                return ([], "no category sitemap found in index");
            }

            // Step 2: Parse each category sitemap and collect every in-domain path.
            var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var sitemapUrl in categorySitemapUrls)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var sitemapXml = await GetStringAsync(sitemapUrl, ct);
                    var sitemap = XDocument.Parse(sitemapXml);
                    var sNs = sitemap.Root?.GetDefaultNamespace() ?? XNamespace.None;

                    foreach (var loc in sitemap.Descendants(sNs + "loc").Select(e => e.Value.Trim()))
                    {
                        if (!loc.StartsWith(BaseUrl, StringComparison.OrdinalIgnoreCase)) continue;

                        // Extract path after domain: "/frescos/fruta-e-legumes/"
                        var path = loc[BaseUrl.Length..].Split('?')[0].Trim('/');
                        if (string.IsNullOrEmpty(path)) continue;
                        discovered.Add(path);
                    }

                    Logger.LogInformation(
                        "Continente: parsed sitemap '{Url}', {Count} paths so far",
                        sitemapUrl, discovered.Count);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Continente: failed to parse category sitemap '{Url}'", sitemapUrl);
                }
            }

            if (discovered.Count == 0)
            {
                Logger.LogWarning("Continente: category sitemaps yielded no matching paths, discovery failed");
                return ([], "category sitemaps yielded no matching paths");
            }

            var selected = SelectCategories(discovered);
            Logger.LogInformation("Continente: {Total} sitemap paths -> {Count} categories to scrape",
                discovered.Count, selected.Count);
            return (selected, null);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Continente: sitemap category discovery failed");
            return ([], ex.Message);
        }
    }

    private async Task ScrapeCategory(
        string categorySlug,
        HashSet<string> seen,
        List<ScrapedProduct> products,
        List<ScrapeOutcome> outcomes,
        List<string> warnings,
        CancellationToken ct)
    {
        var outcome = new ScrapeOutcome
        {
            Kind = OutcomeKind.Category,
            Key = categorySlug,
            Url = $"{BaseUrl}/{categorySlug}/?start=0&sz={PageSize}&srule=Continente"
        };
        outcomes.Add(outcome);
        PagingGuard? guard = null;

        var start = 0;
        var step = DefaultPageStep;
        var staleSincePages = 0;
        while (true)
        {
            if (Halted) { outcome.StopReason ??= $"run halted: {HaltReason}"; return; }

            var url = $"{BaseUrl}/{categorySlug}/?start={start}&sz={PageSize}&srule=Continente";
            var document = await GetHtmlAsync(url, outcome, ct);
            if (document is null)
            {
                Logger.LogError("Continente: failed to fetch category '{Cat}' start={Start}: {Err}",
                    categorySlug, start, outcome.FirstError);
                return;
            }

            var tiles = document.QuerySelectorAll(".product-tile").ToList();
            var skippedTiles = 0;
            var parsed = new List<ScrapedProduct>();
            foreach (var tile in tiles)
            {
                try
                {
                    var p = ParseTile(tile);
                    if (p is not null) parsed.Add(p);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    skippedTiles++;
                }
            }
            if (skippedTiles > 0)
                Logger.LogWarning("Continente: skipped {Skipped} malformed tile(s) in '{Cat}' start={Start}",
                    skippedTiles, categorySlug, start);
            if (start == 0)
            {
                outcome.SiteTotal = ParseTotal(document);
                MarkFirstPage(outcome, tiles.Count, parsed.Count, contentPageWhenNoCounter: true);
                step = ParseCounterStep(document) ?? DefaultPageStep;
            }
            outcome.ProductsListed += parsed.Count;

            var newOnPage = 0;
            foreach (var product in parsed)
            {
                if (seen.Add(product.ExternalId))
                {
                    products.Add(product);
                    outcome.ProductsFound++;
                    newOnPage++;
                }
            }
            staleSincePages = newOnPage > 0 ? 0 : staleSincePages + 1;

            ReportProgress(outcome.Key, start, products.Count, outcome.SiteTotal);
            guard ??= new PagingGuard(outcome.Key, null, outcome.SiteTotal, counterlessRepeatEndsListing: true);
            var verdict = guard.Observe(parsed.Select(p => p.ExternalId).ToList(), start, tiles.Count);
            outcome.StopReason = outcome.Result == OutcomeResult.ContentPage ? outcome.StopReason : guard.StopReason;
            switch (verdict)
            {
                case PagingVerdict.Loop:
                    ReportLoop(guard, warnings);
                    return;
                case PagingVerdict.NaturalEnd:
                    return;
            }

            if (staleSincePages >= MaxPagesWithoutNewProducts)
            {
                outcome.StopReason = $"no new products for {MaxPagesWithoutNewProducts} consecutive pages at offset {start}";
                Logger.LogInformation("Continente: '{Cat}' stopped at offset {Start}: {Reason}",
                    categorySlug, start, outcome.StopReason);
                return;
            }

            // Advance by the site's page step, never past the tiles served (seen set dedupes the overlap).
            start += Math.Min(step, tiles.Count);
        }
    }

    private static ScrapedProduct? ParseTile(IElement tile)
    {
        var impressionJson = tile.GetAttribute("data-product-tile-impression");
        if (string.IsNullOrEmpty(impressionJson)) return null;

        JsonElement impression;
        try
        {
            impression = JsonSerializer.Deserialize<JsonElement>(impressionJson);
        }
        catch
        {
            return null;
        }

        // Fields can be JSON null or a different type; never let GetString/GetDecimal throw.
        static string? ReadString(JsonElement el, string prop, bool allowNumber = false) =>
            !el.TryGetProperty(prop, out var v) ? null
            : v.ValueKind == JsonValueKind.String ? v.GetString()
            : allowNumber && v.ValueKind == JsonValueKind.Number ? v.GetRawText()
            : null;

        var pid = ReadString(impression, "id", allowNumber: true);
        var name = ReadString(impression, "name");
        var brand = ReadString(impression, "brand");
        var categoryPath = ReadString(impression, "category");
        decimal price = impression.TryGetProperty("price", out var priceEl)
            && priceEl.ValueKind == JsonValueKind.Number
            && priceEl.TryGetDecimal(out var parsedPrice) ? parsedPrice : 0;

        if (string.IsNullOrEmpty(pid) || string.IsNullOrEmpty(name) || price <= 0)
            return null;

        // Extract leaf category
        var category = categoryPath?.Split('/').LastOrDefault();

        // Unit price from secondary price element
        var unitPriceText = tile.QuerySelector(".pwc-tile--price-secondary")?.TextContent;
        var unitPrice = ParseUnitPrice(unitPriceText);

        // Promotion detection
        var pricesWrapperText = tile.QuerySelector(".prices-wrapper")?.TextContent ?? string.Empty;
        var hasBadge = tile.QuerySelector("[class*='badge'][class*='discount'], [class*='promo-badge']") is not null;
        var isPromo = hasBadge || pricesWrapperText.Contains("PVPR", StringComparison.OrdinalIgnoreCase);

        // Image URL
        var imgEl = tile.QuerySelector("img");
        var imageUrl = imgEl?.GetAttribute("src") ?? imgEl?.GetAttribute("data-src");
        if (imageUrl?.Contains("badge") == true) // skip badge images
            imageUrl = null;

        // Product URL
        var linkHref = tile.QuerySelector("a[href*='/produto/']")?.GetAttribute("href");
        var sourceUrl = linkHref is not null
            ? (linkHref.StartsWith("http") ? linkHref : $"{BaseUrl}{linkHref}")
            : null;

        // Size and unit: try name first, then full tile text (e.g., "emb. 1 lt")
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
        // "0,90€/lt" or "1,08€/L" → take part before '€'
        var part = text.Split('€')[0].Trim()
            .Replace(",", ".")
            .Replace(" ", "");
        return decimal.TryParse(part, NumberStyles.Any, CultureInfo.InvariantCulture, out var val) && val > 0
            ? val : null;
    }

    private static int? ParseTotal(IDocument doc)
    {
        var text = doc.QuerySelector(".search-results-products-counter")?.TextContent;
        string? number = null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            var m = CounterRegex().Match(text);
            if (m.Success) number = m.Groups[2].Value;
        }
        if (number is null)
        {
            var count = doc.QuerySelector(".product-count")?.TextContent;
            if (!string.IsNullOrWhiteSpace(count))
            {
                var m = FirstNumberRegex().Match(count);
                if (m.Success) number = m.Value;
            }
        }
        if (number is null) return null;
        var digits = number.Replace(",", "").Replace(".", "");
        return int.TryParse(digits, out var val) && val > 0 ? val : null;
    }

    /// <summary>The first-page counter's "shown" number ("35" in "35 de 1080"), which is the site's real page step.</summary>
    private static int? ParseCounterStep(IDocument doc)
    {
        var text = doc.QuerySelector(".search-results-products-counter")?.TextContent;
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = CounterRegex().Match(text);
        if (!m.Success) return null;
        var digits = m.Groups[1].Value.Replace(",", "").Replace(".", "");
        return int.TryParse(digits, out var val) && val > 0 ? val : null;
    }

    [GeneratedRegex(@"(\d[\d.,]*)\s+de\s+([\d.,]+)\s+produtos", RegexOptions.IgnoreCase)]
    private static partial Regex CounterRegex();

    [GeneratedRegex(@"\d[\d.,]*")]
    private static partial Regex FirstNumberRegex();

    // ---- DTOs for SFCC store finder response -----------------------------------

    private sealed class ContinenteStoreFinderResponse
    {
        public List<ContinenteStoreDto> Stores { get; set; } = [];
    }

    private sealed class ContinenteStoreDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Address1 { get; set; }
        public string? City { get; set; }
        public string? PostalCode { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
    }
}
