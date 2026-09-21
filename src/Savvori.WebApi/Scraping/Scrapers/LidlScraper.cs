using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Savvori.Shared;

namespace Savvori.WebApi.Scraping.Scrapers;

/// <summary>
/// Scraper for Lidl Portugal (lidl.pt) via the JSON search API the storefront itself uses.
/// Endpoint: /q/api/search?q={query}&amp;offset={n}&amp;fetchsize=48&amp;locale=pt_PT&amp;assortment=PT&amp;version=2.1.0
/// An empty query lists the whole online assortment (a few hundred items, food and non-food),
/// so only items with category "Food" and a price are kept. Prices are Lidl's published prices,
/// most items are in-store only. Note: the API answers 406 to a strict "Accept: application/json".
/// </summary>
public sealed partial class LidlScraper : BaseHttpScraper
{
    public override string StoreChainSlug => "lidl";

    private const string BaseUrl = "https://www.lidl.pt";
    private const int FetchSize = 48;
    private const int MaxOffset = 2000; // safety cap; the API allows up to 1000 per request

    // The empty query lists only part of the assortment; grocery terms surface the rest.
    private static readonly string[] GrocerySearchTerms =
    [
        "leite", "iogurte", "queijo", "ovos", "carne", "peixe",
        "fruta", "legumes", "bebidas", "congelados", "pão",
        "arroz", "massa", "azeite", "chocolate", "bolachas"
    ];

    public LidlScraper(
        IHttpClientFactory httpClientFactory,
        ILogger<LidlScraper> logger)
        : base(httpClientFactory, logger, "lidl")
    {
    }

    public override async Task<IReadOnlyList<ScrapedProduct>> ScrapeProductsAsync(
        string? category = null, CancellationToken ct = default)
    {
        var seen = new HashSet<string>();
        var products = new List<ScrapedProduct>();
        string[] queries = category is not null ? [category] : ["", .. GrocerySearchTerms];

        foreach (var query in queries)
        {
            ct.ThrowIfCancellationRequested();
            await ScrapeQuery(query, seen, products, ct);
        }

        Logger.LogInformation("Lidl: scraped {Count} distinct food products", products.Count);
        return products;
    }

    private async Task ScrapeQuery(
        string query, HashSet<string> seen, List<ScrapedProduct> products, CancellationToken ct)
    {
        for (var offset = 0; offset < MaxOffset; offset += FetchSize)
        {
            var url = $"{BaseUrl}/q/api/search?q={Uri.EscapeDataString(query)}&offset={offset}" +
                      $"&fetchsize={FetchSize}&locale=pt_PT&assortment=PT&version=2.1.0";

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(await GetStringAsync(url, ct));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Lidl: failed to fetch query='{Query}' offset={Offset}", query, offset);
                return;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                    return;

                foreach (var item in items.EnumerateArray())
                {
                    var product = ParseItem(item);
                    if (product is not null && seen.Add(product.ExternalId))
                        products.Add(product);
                }

                var numFound = root.TryGetProperty("numFound", out var nf) && nf.TryGetInt32(out var n) ? n : 0;
                if (items.GetArrayLength() == 0 || offset + FetchSize >= numFound) return;
            }

            await Task.Delay(500, ct);
        }
    }

    public override Task<IReadOnlyList<ScrapedStoreLocation>> ScrapeStoreLocationsAsync(
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ScrapedStoreLocation>>([]);
    }

    private static ScrapedProduct? ParseItem(JsonElement item)
    {
        if (!item.TryGetProperty("gridbox", out var gridbox) ||
            !gridbox.TryGetProperty("data", out var data))
            return null;

        if (GetString(data, "category") != "Food") return null;

        var id = data.TryGetProperty("itemId", out var idEl) ? idEl.ToString() : null;
        var name = GetString(data, "fullTitle") ?? GetString(data, "title");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) return null;

        if (!data.TryGetProperty("price", out var priceEl)) return null;
        if (!priceEl.TryGetProperty("price", out var priceValue) || !priceValue.TryGetDecimal(out var price) || price <= 0)
            return null;

        // Discount: "deletedPrice" is the struck-through price
        var isPromo = false;
        string? promoDesc = null;
        if (priceEl.TryGetProperty("discount", out var discount) &&
            discount.TryGetProperty("deletedPrice", out var oldEl) &&
            oldEl.TryGetDecimal(out var oldPrice) && oldPrice > price)
        {
            isPromo = true;
            promoDesc = discount.TryGetProperty("percentageDiscount", out var pct) && pct.TryGetInt32(out var p) && p > 0
                ? $"-{p}%"
                : $"Antes {oldPrice.ToString("0.00", CultureInfo.InvariantCulture)} €";
        }

        var packaging = priceEl.TryGetProperty("packaging", out var pk) ? GetString(pk, "text") : null;
        var baseText = priceEl.TryGetProperty("basePrice", out var bp) && bp.ValueKind == JsonValueKind.Object
            ? GetString(bp, "text") : null;
        var (unitPrice, unit) = ParseBasePrice(baseText);

        var sizeUnit = ProductNormalizer.ExtractSizeAndUnit(CleanPackaging(packaging))
                       ?? ProductNormalizer.ExtractSizeAndUnit(name);

        var brand = data.TryGetProperty("brand", out var brandEl) ? GetString(brandEl, "name") : null;
        var path = GetString(data, "canonicalUrl") ?? GetString(data, "canonicalPath");

        return new ScrapedProduct(
            Name: name,
            Brand: string.IsNullOrWhiteSpace(brand) || brand == "-" ? null : brand, // "-" = unbranded
            Category: ExtractCategory(data),
            Price: price,
            UnitPrice: unitPrice,
            EAN: null,
            ExternalId: id,
            ImageUrl: GetString(data, "image"),
            SourceUrl: path is null ? BaseUrl : $"{BaseUrl}{path}",
            IsPromotion: isPromo,
            PromotionDescription: promoDesc,
            Unit: unit ?? sizeUnit?.Unit ?? ProductUnit.Unit,
            SizeValue: sizeUnit?.SizeValue
        );
    }

    // "Mundos de necessidade/Alimentos e quase alimentos/Queijos, laticínios e ovos/Leite e natas" → "Leite e natas"
    private static string? ExtractCategory(JsonElement data)
    {
        if (!data.TryGetProperty("keyfacts", out var kf)) return null;
        var path = GetString(kf, "wonCategoryPrimary");
        var leaf = path?.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return string.IsNullOrEmpty(leaf) ? null : leaf;
    }

    // "Cada emb. 1 L" / "Pack 8x200 ml" / "Emb. 100 g" → "1 L" / "8x200 ml" / "100 g"
    private static string CleanPackaging(string? text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : PackagingPrefix().Replace(text, string.Empty).Trim();

    // "1 L = 1,75" / "1 kg = 2,95" / "100 g = 0,87" → price per kg or per L
    private static (decimal? UnitPrice, ProductUnit? Unit) ParseBasePrice(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);
        var m = BasePricePattern().Match(text);
        if (!m.Success) return (null, null);

        if (!TryParse(m.Groups["qty"].Value, out var qty) || qty <= 0 ||
            !TryParse(m.Groups["price"].Value, out var price) || price <= 0)
            return (null, null);

        return m.Groups["unit"].Value.ToLowerInvariant() switch
        {
            "kg" => (Math.Round(price / qty, 2), ProductUnit.Kg),
            "g" => (Math.Round(price * 1000m / qty, 2), ProductUnit.Kg),
            "l" => (Math.Round(price / qty, 2), ProductUnit.L),
            "cl" => (Math.Round(price * 100m / qty, 2), ProductUnit.L),
            "ml" => (Math.Round(price * 1000m / qty, 2), ProductUnit.L),
            _ => (null, null)
        };
    }

    private static bool TryParse(string s, out decimal value) =>
        decimal.TryParse(s.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out value);

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [GeneratedRegex(@"^\s*(?:cada\s+)?(?:emb\.?|pack)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex PackagingPrefix();

    [GeneratedRegex(@"(?<qty>\d+(?:[.,]\d+)?)\s*(?<unit>kg|g|l|cl|ml)\s*=\s*(?<price>\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex BasePricePattern();
}
