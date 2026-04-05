using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    // Browse all grocery-relevant Pingo Doce categories using the site's actual category IDs
    private static readonly string[] GroceryCategories =
    [
        "ec_frutasevegetais_100",         // fruits & vegetables
        "ec_talho_200",                   // meat
        "ec_charcutariaqueijos_500",       // charcuterie & cheese
        "ec_ovos_600",                    // eggs
        "ec_leitebebidasvegetais_900",     // milk & plant drinks
        "ec_iogurtessobremesas_800",       // yogurts & desserts
        "ec_congelados_1000",             // frozen
        "ec_cafechaachocolatados_1100",    // coffee, tea & hot drinks
        "ec_bolachascereaisguloseimas_1200", // cookies, cereals & sweets
        "ec_mercearia_1300",              // grocery staples (pasta, rice, sauces…)
        "ec_aguassumosrefrigerantes_1400", // water, juices & soft drinks
    ];

    public PingoDoceScraper(
        IHttpClientFactory httpClientFactory,
        ILogger<PingoDoceScraper> logger)
        : base(httpClientFactory, logger, "pingodoce")
    {
    }

    public override async Task<IReadOnlyList<ScrapedProduct>> ScrapeProductsAsync(
        string? category = null, CancellationToken ct = default)
    {
        var seen = new HashSet<string>();
        var products = new List<ScrapedProduct>();

        if (category is not null)
        {
            await ScrapeCategory(category, seen, products, ct);
        }
        else
        {
            foreach (var cat in GroceryCategories)
            {
                ct.ThrowIfCancellationRequested();
                await ScrapeCategory(cat, seen, products, ct);
                await Task.Delay(800, ct);
            }
        }

        Logger.LogInformation("PingoDoce: scraped {Count} distinct products", products.Count);
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
        CancellationToken ct)
    {
        var start = 0;
        int? total = null;

        do
        {
            // Use category browse (cgid=) with ajax format — server-renders product tiles
            var url = $"{SearchUrl}?cgid={Uri.EscapeDataString(categoryId)}&format=ajax&start={start}&sz={PageSize}";
            IDocument document;

            try
            {
                document = await GetHtmlAsync(url, ct);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "PingoDoce: failed to fetch category='{Category}' start={Start}", categoryId, start);
                break;
            }

            var tiles = document.QuerySelectorAll(".product-tile-pd").ToList();
            if (tiles.Count == 0) break;

            foreach (var tile in tiles)
            {
                var product = ParseTile(tile);
                if (product is null) continue;
                if (seen.Add(product.ExternalId))
                    products.Add(product);
            }

            if (total is null)
            {
                var countText = document.QuerySelector(".results-hits, [class*='result-count']")?.TextContent;
                total = ParseCount(countText);
            }

            // Fewer tiles than requested means this is the last page
            if (tiles.Count < PageSize) break;

            start += PageSize;

            if (start < (total ?? int.MaxValue))
                await Task.Delay(500, ct);

        } while (start < (total ?? int.MaxValue));
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
        decimal price = item.TryGetProperty("price", out var priceEl) ? priceEl.GetDecimal() : 0;
        decimal discount = item.TryGetProperty("discount", out var discEl) ? discEl.GetDecimal() : 0;

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
            var rawSize = match.Groups["size"].Value.Replace(",", ".");
            var rawUnit = match.Groups["unit"].Value.ToLowerInvariant();
            var rawUnitPrice = match.Groups["uprice"].Value.Replace(",", ".");

            decimal.TryParse(rawSize, NumberStyles.Any, CultureInfo.InvariantCulture, out var sizeVal);
            decimal.TryParse(rawUnitPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out var unitPriceVal);

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

    private static int? ParseCount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = CountDigits().Match(text);
        if (!match.Success) return null;
        var digits = match.Value.Replace(",", "").Replace(".", "");
        return int.TryParse(digits, out var val) ? val : null;
    }

    // Matches patterns like "1 L | 0,86 €/L" or "500 g | 2,50 €/kg"
    [GeneratedRegex(
        @"(?<size>[\d,]+)\s*(?<unit>kg|g|l|ml|cl)\s*\|\s*(?<uprice>[\d,]+)\s*€/(?:kg|g|l|ml)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizeUnitPricePattern();

    // Matches a number that may use comma/dot as thousand separators (e.g., "11,028" or "5.576")
    [GeneratedRegex(@"[\d,.]+")]
    private static partial Regex CountDigits();

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

