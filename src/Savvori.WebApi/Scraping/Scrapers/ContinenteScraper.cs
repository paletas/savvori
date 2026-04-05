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

    // Geographic sweep centres — 300 km radius covers all of mainland Portugal from two points.
    private static readonly (double Lat, double Lng)[] SweepCentres =
    [
        (41.15, -8.61),   // Porto  — covers north + centre
        (37.02, -7.93),   // Faro   — covers Algarve + Alentejo south
    ];

    private const int MaxPagesPerCategory = 50;  // safety cap (~2,400 products per category)
    private const string SitemapIndexUrl = $"{BaseUrl}/sitemap_index.xml";

    // Top-level slugs to include when discovering categories.
    // Extend this list to capture non-grocery departments as well.
    private static readonly HashSet<string> AllowedTopSlugs = new(StringComparer.OrdinalIgnoreCase)
    {
        "frescos", "laticinios-e-ovos", "mercearia", "congelados",
        "peixaria-e-talho", "bebidas-e-garrafeira", "bio-e-saudavel",
        "padaria-e-pastelaria", "higiene-e-beleza", "limpeza-e-casa",
        "pet", "bebe"
    };

    // Last-resort fallback: top-level slugs used when all discovery mechanisms fail.
    private static readonly string[] FallbackCategories =
    [
        "frescos", "laticinios-e-ovos", "mercearia", "congelados",
        "peixaria-e-talho", "bebidas-e-garrafeira", "bio-e-saudavel",
        "padaria-e-pastelaria"
    ];

    public ContinenteScraper(
        IHttpClientFactory httpClientFactory,
        ILogger<ContinenteScraper> logger)
        : base(httpClientFactory, logger, "continente")
    {
    }

    public override async Task<IReadOnlyList<ScrapedProduct>> ScrapeProductsAsync(
        string? category = null, CancellationToken ct = default)
    {
        IReadOnlyList<string> categories;
        if (category is not null)
        {
            categories = [category];
        }
        else
        {
            categories = await DiscoverCategoryPathsAsync(ct);
            await Task.Delay(500, ct);
        }

        var seen = new HashSet<string>();
        var products = new List<ScrapedProduct>();

        foreach (var cat in categories)
        {
            ct.ThrowIfCancellationRequested();
            await ScrapeCategory(cat, seen, products, ct);
            await Task.Delay(300, ct);
        }

        Logger.LogInformation("Continente: scraped {Count} distinct products across {Cats} categories",
            products.Count, categories.Count);
        return products;
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
    /// Discovers category paths from the Continente sitemap.
    /// Fetches the sitemap index, locates the category sitemap (excluding refinement sitemaps),
    /// parses category URLs, filters to allowed top-level slugs, and prunes parent paths so only
    /// leaf categories are scraped. Falls back to <see cref="FallbackCategories"/> on failure.
    /// </summary>
    private async Task<IReadOnlyList<string>> DiscoverCategoryPathsAsync(CancellationToken ct)
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
                Logger.LogWarning("Continente: no category sitemap found in {Url}, using fallback", SitemapIndexUrl);
                return FallbackCategories;
            }

            // Step 2: Parse each category sitemap and collect matching paths.
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

                        var firstSegment = path.Split('/')[0];
                        if (!AllowedTopSlugs.Contains(firstSegment)) continue;

                        discovered.Add(path);
                    }

                    Logger.LogInformation(
                        "Continente: parsed sitemap '{Url}', {Count} matching paths so far",
                        sitemapUrl, discovered.Count);

                    await Task.Delay(300, ct);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Continente: failed to parse category sitemap '{Url}'", sitemapUrl);
                }
            }

            if (discovered.Count == 0)
            {
                Logger.LogWarning("Continente: category sitemaps yielded no matching paths, using fallback");
                return FallbackCategories;
            }

            // Step 3: Prune parent paths — only scrape leaves to avoid duplicate products.
            // If both "frescos/fruta" and "frescos/fruta/maca" exist, drop "frescos/fruta".
            var paths = discovered.OrderByDescending(p => p).ToList();
            var leaves = paths
                .Where(p => !paths.Any(other =>
                    other != p && other.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            Logger.LogInformation(
                "Continente: {Total} category paths in sitemap → {Leaves} leaf categories to scrape",
                paths.Count, leaves.Count);

            return leaves;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Continente: sitemap category discovery failed, using fallback list");
            return FallbackCategories;
        }
    }

    private async Task ScrapeCategory(
        string categorySlug,
        HashSet<string> seen,
        List<ScrapedProduct> products,
        CancellationToken ct)
    {
        var start = 0;
        int? total = null;
        var pageCount = 0;

        do
        {
            var url = $"{BaseUrl}/{categorySlug}/?start={start}&sz={PageSize}&srule=Continente";
            IDocument document;

            try
            {
                document = await GetHtmlAsync(url, ct);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Continente: failed to fetch category '{Cat}' start={Start}", categorySlug, start);
                break;
            }

            var tiles = document.QuerySelectorAll(".product-tile").ToList();
            if (tiles.Count == 0) break;

            var newOnThisPage = 0;
            foreach (var tile in tiles)
            {
                var product = ParseTile(tile);
                if (product is null) continue;
                if (seen.Add(product.ExternalId))
                {
                    products.Add(product);
                    newOnThisPage++;
                }
            }

            // No new products on this page → we've looped back to already-seen results; stop.
            if (newOnThisPage == 0) break;

            if (total is null)
            {
                var countText = document.QuerySelector(".product-count")?.TextContent;
                total = ParseCount(countText);
            }

            // Fewer tiles than requested means this is the last page.
            if (tiles.Count < PageSize) break;

            start += PageSize;
            pageCount++;

            // Safety cap: bail out if pagination produces an unexpectedly large number of pages.
            if (pageCount >= MaxPagesPerCategory)
            {
                Logger.LogWarning(
                    "Continente: hit page cap ({Max}) for category '{Cat}', stopping pagination",
                    MaxPagesPerCategory, categorySlug);
                break;
            }

            if (start < (total ?? int.MaxValue))
                await Task.Delay(200, ct);

        } while (start < (total ?? int.MaxValue));
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

        var pid = impression.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        var name = impression.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
        var brand = impression.TryGetProperty("brand", out var brandEl) ? brandEl.GetString() : null;
        var categoryPath = impression.TryGetProperty("category", out var catEl) ? catEl.GetString() : null;
        decimal price = impression.TryGetProperty("price", out var priceEl) ? priceEl.GetDecimal() : 0;

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
            ?? ProductNormalizer.ExtractSizeAndUnit(tile.TextContent ?? string.Empty);

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

    private static int? ParseCount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = CountDigits().Match(text);
        if (!match.Success) return null;
        var digits = match.Value.Replace(",", "").Replace(".", "");
        return int.TryParse(digits, out var val) ? val : null;
    }

    [GeneratedRegex(@"[\d,.]+")]
    private static partial Regex CountDigits();

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
