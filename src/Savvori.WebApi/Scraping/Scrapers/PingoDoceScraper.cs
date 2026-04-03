using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Savvori.Shared;

namespace Savvori.WebApi.Scraping.Scrapers;

public sealed partial class PingoDoceScraper : BaseHttpScraper
{
    public override string StoreChainSlug => "pingodoce";

    private const string SearchUrl =
        "https://www.pingodoce.pt/on/demandware.store/Sites-pingo-doce-Site/default/Search-Show";
    private const int PageSize = 24;

    // Browse all grocery-relevant Pingo Doce categories
    private static readonly string[] GroceryCategories =
    [
        "leite-e-bebidas-vegetais", "iogurtes-e-sobremesas", "queijos-e-charcutaria", "ovos",
        "carne", "peixe-e-marisco", "frutas-e-legumes",
        "mercearia", "congelados", "bebidas",
        "padaria-e-pastelaria", "bio-e-saudavel"
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
            await ScrapeSearch(category, seen, products, ct);
        }
        else
        {
            foreach (var cat in GroceryCategories)
            {
                ct.ThrowIfCancellationRequested();
                await ScrapeSearch(cat, seen, products, ct);
                await Task.Delay(800, ct);
            }
        }

        Logger.LogInformation("PingoDoce: scraped {Count} distinct products", products.Count);
        return products;
    }

    public override Task<IReadOnlyList<ScrapedStoreLocation>> ScrapeStoreLocationsAsync(
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ScrapedStoreLocation>>([]);
    }

    private async Task ScrapeSearch(
        string query,
        HashSet<string> seen,
        List<ScrapedProduct> products,
        CancellationToken ct)
    {
        var start = 0;
        int? total = null;

        do
        {
            var url = $"{SearchUrl}?q={Uri.EscapeDataString(query)}&start={start}&sz={PageSize}";
            IDocument document;

            try
            {
                document = await GetHtmlAsync(url, ct);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "PingoDoce: failed to fetch query='{Query}' start={Start}", query, start);
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

            start += PageSize;

            if (start < (total ?? int.MaxValue))
                await Task.Delay(500, ct);

        } while (start < (total ?? 0) + PageSize);
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
            ? (linkHref.StartsWith("http") ? linkHref : $"https://www.pingodoce.pt{linkHref}")
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
        return match.Success ? int.Parse(match.Value) : null;
    }

    // Matches patterns like "1 L | 0,86 €/L" or "500 g | 2,50 €/kg"
    [GeneratedRegex(
        @"(?<size>[\d,]+)\s*(?<unit>kg|g|l|ml|cl)\s*\|\s*(?<uprice>[\d,]+)\s*€/(?:kg|g|l|ml)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizeUnitPricePattern();

    [GeneratedRegex(@"\d+")]
    private static partial Regex CountDigits();
}
