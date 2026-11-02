using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AngleSharp.Dom;
using Savvori.Shared;

namespace Savvori.WebApi.Scraping.Scrapers;

public sealed partial class PingoDoceScraper : BaseHttpScraper
{
    public override string StoreChainSlug => "pingodoce";

    private const string BaseUrl = "https://www.pingodoce.pt";
    private const string SearchUrl =
        "https://www.pingodoce.pt/on/demandware.store/Sites-pingo-doce-Site/default/Search-Show";
    private const string StoreFinderUrl =
        "https://www.pingodoce.pt/on/demandware.store/Sites-pingo-doce-Site/default/Stores-FindStores";
    private const int PageSize = 24;

    // Geographic sweep centres — 300 km radius covers all of mainland Portugal from two points.
    private static readonly (double Lat, double Lng)[] SweepCentres =
    [
        (41.15, -8.61),   // Porto  — covers north + centre
        (37.02, -7.93),   // Faro   — covers Algarve + Alentejo south
    ];

    private const string SitemapIndexUrl = $"{BaseUrl}/home/sitemap_index.xml";
    private const string ProductsPrefix = "/home/produtos/";

    // Real category id requested by the canary probe.
    private const string CanaryCgid = "ec_frutasevegetais_100";

    // Brand and promotion pages repeat products already listed under the departments.
    private static readonly string[] ExcludedSections = ["as-nossas-marcas", "promocoes"];

    // Seasonal sections are absent from the nav menu; their cgid is read from the section's own page.
    private static readonly string[] SeasonalSections = ["natal-e-ano-novo", "pascoa", "santos-populares", "feira-do-bebe"];

    // Technical sections that hold no real assortment.
    private static readonly string[] IgnoredSections = ["freshpage", "unclassified", "valor-de-deposito"];

    public PingoDoceScraper(
        IHttpClientFactory httpClientFactory,
        ILogger<PingoDoceScraper> logger)
        : base(httpClientFactory, logger, "pingodoce")
    {
        RequestCeiling = 2000;
    }

    public override async Task<ScrapeRunResult> ScrapeProductsAsync(
        string? category = null, CancellationToken ct = default)
    {
        BeginRun();
        var warnings = new List<string>();
        var seen = new HashSet<string>();
        var products = new List<ScrapedProduct>();
        var outcomes = new List<ScrapeOutcome>();
        var seasonalCgids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (category is not null)
        {
            await ScrapeCategory(category, seen, products, outcomes, warnings, false, ct);
        }
        else
        {
            var (sections, discWarnings, failure) = await DiscoverSectionsAsync(ct);
            if (failure is not null) return DiscoveryFailed(SitemapIndexUrl, failure);
            warnings.AddRange(discWarnings);

            SetKeysTotal(sections.Count);
            foreach (var (slug, cgid) in sections)
            {
                if (Halted) break;
                ct.ThrowIfCancellationRequested();
                var seasonal = SeasonalSections.Contains(slug, StringComparer.OrdinalIgnoreCase);
                if (seasonal) seasonalCgids.Add(cgid!);
                await ScrapeCategory(cgid!, seen, products, outcomes, warnings, seasonal, ct);
                ReportKeyDone(cgid!, products.Count);
            }
        }

        await ReattemptAsync(outcomes, async old =>
        {
            var tmp = new List<ScrapeOutcome>();
            await ScrapeCategory(old.Key, seen, products, tmp, warnings, seasonalCgids.Contains(old.Key), ct);
            return tmp[0];
        }, ct);

        Logger.LogInformation("PingoDoce: scraped {Count} distinct products", products.Count);
        return BuildResult(products, outcomes, warnings);
    }

    public override Task<CanaryResult> ProbeAsync(CancellationToken ct = default)
        => ProbeHtmlAsync($"{SearchUrl}?cgid={Uri.EscapeDataString(CanaryCgid)}&format=ajax&start=0&sz={PageSize}", d =>
        {
            var t = d.QuerySelectorAll(".product-tile-pd").ToList();
            return (t.Count, t.Count(x => ParseTile(x) is not null));
        }, ct);

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
                var response = await GetJsonAsync<PingoDoceStoreFinderResponse>(url, ct);
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
                    "PingoDoce: centre ({Lat},{Lng}) returned {Count} stores (total: {Total})",
                    lat, lng, response.Stores.Count, locations.Count);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "PingoDoce: failed to fetch store locations (centre {Lat},{Lng})", lat, lng);
            }

            await Task.Delay(500, ct);
        }

        Logger.LogInformation("PingoDoce: {Count} distinct store locations scraped", locations.Count);
        return locations;
    }

    private async Task ScrapeCategory(
        string categoryId,
        HashSet<string> seen,
        List<ScrapedProduct> products,
        List<ScrapeOutcome> outcomes,
        List<string> warnings,
        bool seasonal,
        CancellationToken ct)
    {
        var outcome = new ScrapeOutcome
        {
            Kind = OutcomeKind.Category,
            Key = categoryId,
            Url = $"{SearchUrl}?cgid={Uri.EscapeDataString(categoryId)}&format=ajax&start=0&sz={PageSize}"
        };
        outcomes.Add(outcome);
        PagingGuard? guard = null;

        for (var start = 0; ; start += PageSize)
        {
            if (Halted) { outcome.StopReason ??= $"run halted: {HaltReason}"; return; }

            // Use category browse (cgid=) with ajax format — server-renders product tiles
            var url = $"{SearchUrl}?cgid={Uri.EscapeDataString(categoryId)}&format=ajax&start={start}&sz={PageSize}";
            var document = await GetHtmlAsync(url, outcome, ct);
            if (document is null)
            {
                Logger.LogError("PingoDoce: failed to fetch category='{Category}' start={Start}: {Err}",
                    categoryId, start, outcome.FirstError);
                return;
            }

            var tiles = document.QuerySelectorAll(".product-tile-pd").ToList();
            var parsed = tiles.Select(ParseTile).Where(p => p is not null).Select(p => p!).ToList();
            // A seasonal section out of season serves an empty page with no counter: a content page, not a failure.
            if (start == 0) MarkFirstPage(outcome, tiles.Count, parsed.Count, contentPageWhenNoCounter: seasonal);
            outcome.ProductsListed += parsed.Count;

            foreach (var product in parsed)
            {
                if (seen.Add(product.ExternalId))
                {
                    products.Add(product);
                    outcome.ProductsFound++;
                }
            }

            // The ajax tiles show no counter, so there is no per-section total; the API falls back to the last healthy run.
            ReportProgress(outcome.Key, start, products.Count, null, PageSize);
            guard ??= new PagingGuard(outcome.Key, PageSize, null);
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
        }
    }

    /// <summary>
    /// Selects the sections to scrape from the category sitemap locs: the first path segment after
    /// <c>/home/produtos/</c>, without <c>as-nossas-marcas</c> and <c>promocoes</c> (they repeat the
    /// departments), distinct and sorted. Locs outside that prefix are ignored.
    /// </summary>
    public static IReadOnlyList<string> SelectSections(IEnumerable<string> locs)
    {
        var sections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in locs)
        {
            var loc = raw.Trim();
            if (!loc.StartsWith(BaseUrl, StringComparison.OrdinalIgnoreCase)) continue;
            var path = loc[BaseUrl.Length..];
            if (!path.StartsWith(ProductsPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = path[ProductsPrefix.Length..].Split('?', '#')[0];
            var slug = rest.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrEmpty(slug)) continue;
            if (ExcludedSections.Contains(slug, StringComparer.OrdinalIgnoreCase)) continue;
            sections.Add(slug);
        }
        return sections.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Maps each top-level section slug to its cgid, read from the menu anchors
    /// (<c>href="/home/produtos/slug" id="ec_..."</c>). Two-segment links are ignored.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseNavCgids(IDocument doc)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in doc.QuerySelectorAll("a[href^='/home/produtos/'][id^='ec_']"))
        {
            var href = (a.GetAttribute("href") ?? string.Empty).Trim().TrimEnd('/');
            var rest = href.Length > ProductsPrefix.Length ? href[ProductsPrefix.Length..] : string.Empty;
            var id = a.GetAttribute("id");
            if (rest.Length == 0 || rest.Contains('/') || string.IsNullOrEmpty(id)) continue;
            map.TryAdd(rest, id);
        }
        return map;
    }

    /// <summary>
    /// Discovers the sections to scrape. Source: the category sitemap listed in the sitemap index
    /// (sections via <see cref="SelectSections"/>, marcas and promocoes excluded) plus one extra page
    /// (the first section's page) whose menu carries the cgid of every section. A section without a
    /// cgid is skipped with a warning. On failure returns a <c>FailureReason</c> (the run is then
    /// reported as failed discovery).
    /// </summary>
    private async Task<(IReadOnlyList<(string Slug, string? Cgid)> Sections, IReadOnlyList<string> Warnings, string? FailureReason)>
        DiscoverSectionsAsync(CancellationToken ct)
    {
        try
        {
            var indexXml = await GetStringAsync(SitemapIndexUrl, ct);
            var index = XDocument.Parse(indexXml);
            var ns = index.Root?.GetDefaultNamespace() ?? XNamespace.None;
            var sitemapUrl = index.Descendants(ns + "loc")
                .Select(e => e.Value.Trim())
                .FirstOrDefault(u =>
                    u.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
                    !u.Contains("-product", StringComparison.OrdinalIgnoreCase));
            if (sitemapUrl is null)
            {
                Logger.LogWarning("PingoDoce: no category sitemap found in {Url}, discovery failed", SitemapIndexUrl);
                return ([], [], "no category sitemap found in index");
            }

            var sitemapXml = await GetStringAsync(sitemapUrl, ct);
            var sitemap = XDocument.Parse(sitemapXml);
            var sNs = sitemap.Root?.GetDefaultNamespace() ?? XNamespace.None;
            var sections = SelectSections(sitemap.Descendants(sNs + "loc").Select(e => e.Value));
            if (sections.Count == 0)
            {
                Logger.LogWarning("PingoDoce: category sitemap yielded no sections, discovery failed");
                return ([], [], "category sitemap yielded no sections");
            }

            var navUrl = $"{BaseUrl}{ProductsPrefix}{sections[0]}";
            var doc = await GetHtmlAsync(navUrl, new ScrapeOutcome { Url = navUrl }, ct);
            if (doc is null)
            {
                Logger.LogWarning("PingoDoce: nav page {Url} unavailable, discovery failed", navUrl);
                return ([], [], "nav page unavailable");
            }

            var cgids = ParseNavCgids(doc);
            var result = new List<(string Slug, string? Cgid)>();
            var warnings = new List<string>();
            foreach (var slug in sections)
            {
                if (IgnoredSections.Contains(slug, StringComparer.OrdinalIgnoreCase))
                {
                    Logger.LogInformation("Pingo Doce: section '{Slug}' ignored (technical)", slug);
                    continue;
                }

                if (cgids.TryGetValue(slug, out var cgid)) result.Add((slug, cgid));
                else if (SeasonalSections.Contains(slug, StringComparer.OrdinalIgnoreCase))
                {
                    var seasonal = await ResolveSeasonalCgidAsync(slug, ct);
                    if (seasonal is not null) result.Add((slug, seasonal));
                    else Logger.LogInformation("Pingo Doce: seasonal section '{Slug}' not live", slug);
                }
                else warnings.Add($"Pingo Doce: no cgid for section '{slug}'");
            }

            if (result.Count == 0)
            {
                Logger.LogWarning("PingoDoce: no section ids found in menu, discovery failed");
                return ([], warnings, "no section ids found in menu");
            }

            Logger.LogInformation("PingoDoce: {Total} sections -> {Count} with a cgid", sections.Count, result.Count);
            return (result, warnings, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Xml.XmlException)
        {
            Logger.LogWarning(ex, "PingoDoce: section discovery failed");
            return ([], [], ex.Message);
        }
    }

    private async Task<string?> ResolveSeasonalCgidAsync(string slug, CancellationToken ct)
    {
        var url = $"{BaseUrl}{ProductsPrefix}{slug}";
        var doc = await GetHtmlAsync(url, new ScrapeOutcome { Url = url }, ct);
        if (doc is null) return null;

        var candidates = new[]
        {
            doc.QuerySelector("div.get-category")?.GetAttribute("data-category-id"),
            doc.QuerySelector("input.category-id")?.GetAttribute("value"),
            doc.QuerySelector("div.page")?.GetAttribute("data-querystring") is { } qs && qs.StartsWith("cgid=", StringComparison.OrdinalIgnoreCase)
                ? qs["cgid=".Length..] : null,
        };
        return candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && c.StartsWith("ec_", StringComparison.Ordinal));
    }

    private static ScrapedProduct? ParseTile(IElement tile)
    {
        var pid = tile.GetAttribute("data-pid");
        if (string.IsNullOrEmpty(pid)) return null;

        var gtmJson = tile.GetAttribute("data-gtm-info");
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

        if (!gtm.TryGetProperty("items", out var itemsEl) || itemsEl.GetArrayLength() == 0)
            return null;

        var item = itemsEl[0];
        var name = item.TryGetProperty("item_name", out var nameEl) ? nameEl.GetString() : null;
        var brand = item.TryGetProperty("item_brand", out var brandEl) ? brandEl.GetString() : null;
        var category = item.TryGetProperty("item_category", out var catEl) ? catEl.GetString() : null;
        // A JSON null (or non-number) must not throw: treat as 0.
        decimal price = item.TryGetProperty("price", out var priceEl)
            && priceEl.ValueKind == JsonValueKind.Number && priceEl.TryGetDecimal(out var p) ? p : 0;
        decimal discount = item.TryGetProperty("discount", out var discEl)
            && discEl.ValueKind == JsonValueKind.Number && discEl.TryGetDecimal(out var d) ? d : 0;

        if (string.IsNullOrEmpty(name) || price <= 0) return null;

        // Parse size and unit price from tile text
        // Pattern: "1 L | 0,86 €/L" or "500 g | 2,50 €/kg"
        var tileText = tile.TextContent ?? string.Empty;
        var (sizeValue, unit, unitPrice) = ParseSizeAndUnitFromText(tileText, name);

        // Promotion
        var isPromo = discount > 0;
        string? promoDesc = isPromo ? $"Desconto {discount:0.##}€" : null;

        // Image
        var imgEl = tile.QuerySelector("img");
        var imageUrl = imgEl?.GetAttribute("src") ?? imgEl?.GetAttribute("data-src");

        // Product URL
        var linkHref = tile.QuerySelector("a")?.GetAttribute("href");
        var sourceUrl = linkHref is not null
            ? (linkHref.StartsWith("http") ? linkHref : $"{BaseUrl}{linkHref}")
            : null;

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
            PromotionDescription: promoDesc,
            Unit: unit,
            SizeValue: sizeValue
        );
    }

    private static (decimal? SizeValue, ProductUnit Unit, decimal? UnitPrice) ParseSizeAndUnitFromText(
        string text, string productName)
    {
        // Try to find "X L | Y €/L" or "X g | Y €/kg" pattern in tile text
        var match = SizeUnitPricePattern().Match(text);
        if (match.Success)
        {
            var rawUnit = match.Groups["unit"].Value.ToLowerInvariant();

            ProductNormalizer.TryParseDecimal(match.Groups["size"].Value, out var sizeVal);
            ProductNormalizer.TryParseDecimal(match.Groups["uprice"].Value, out var unitPriceVal);

            var unit = rawUnit switch
            {
                "kg" => ProductUnit.Kg,
                "g" or "gr" => ProductUnit.G,
                "l" => ProductUnit.L,
                "ml" => ProductUnit.Ml,
                "cl" => ProductUnit.Ml,
                _ => ProductUnit.Unit
            };

            if (rawUnit == "cl") sizeVal *= 10;

            return (sizeVal > 0 ? sizeVal : null,
                unit,
                unitPriceVal > 0 ? unitPriceVal : null);
        }

        // Fallback: extract from product name
        var sizeUnit = ProductNormalizer.ExtractSizeAndUnit(productName);
        return (sizeUnit?.SizeValue, sizeUnit?.Unit ?? ProductUnit.Unit, null);
    }

    // Matches patterns like "1 L | 0,86 €/L" or "500 g | 2,50 €/kg"
    [GeneratedRegex(
        @"(?<![\d.,])(?<size>\d+(?:[.,]\d+)?)\s*(?<unit>kg|g|l|ml|cl)\s*\|\s*(?<uprice>\d+(?:[.,]\d+)?)\s*€/(?:kg|g|l|ml)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizeUnitPricePattern();

    // ---- DTOs for SFCC store finder response -----------------------------------

    private sealed class PingoDoceStoreFinderResponse
    {
        public List<PingoDoceStoreDto> Stores { get; set; } = [];
    }

    private sealed class PingoDoceStoreDto
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

