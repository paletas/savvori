using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Savvori.Shared;

namespace Savvori.WebApi.Scraping.Scrapers;

/// <summary>
/// Scraper for Auchan Portugal (auchan.pt).
/// Uses the SFCC platform — products are in .product-tile elements with data-gtm JSON.
/// Search URL: https://www.auchan.pt/pt/pesquisa?q={query}&page={page}
/// </summary>
public sealed partial class AuchanScraper : BaseHttpScraper
{
    public override string StoreChainSlug => "auchan";

    private const string BaseUrl = "https://www.auchan.pt";
    private const string SearchBase = "https://www.auchan.pt/pt/pesquisa";
    private const int PageSize = 24;

    private static readonly string[] GrocerySearchTerms =
    [
        "leite", "iogurte", "queijo", "fruta", "legumes",
        "carne", "charcutaria", "peixe", "marisco",
        "mercearia", "congelados", "bebidas", "pão", "pastelaria",
        "ovos", "bio", "arroz", "massa", "azeite"
    ];

    public AuchanScraper(
        IHttpClientFactory httpClientFactory,
        ILogger<AuchanScraper> logger)
        : base(httpClientFactory, logger, "auchan")
    {
    }

    public override async Task<IReadOnlyList<ScrapedProduct>> ScrapeProductsAsync(
        string? category = null, CancellationToken ct = default)
    {
        var terms = category is not null ? [category] : GrocerySearchTerms;
        var seen = new HashSet<string>();
        var products = new List<ScrapedProduct>();

        foreach (var term in terms)
        {
            ct.ThrowIfCancellationRequested();
            await ScrapeSearch(term, seen, products, ct);
            await Task.Delay(800, ct);
        }

        Logger.LogInformation("Auchan: scraped {Count} distinct products", products.Count);
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
        var page = 1;
        int? total = null;
        var scraped = 0;

        do
        {
            var url = $"{SearchBase}?q={Uri.EscapeDataString(query)}&page={page}";
            IDocument document;

            try
            {
                document = await GetHtmlAsync(url, ct);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Auchan: failed to fetch query='{Query}' page={Page}", query, page);
                break;
            }

            var tiles = document.QuerySelectorAll(".product-tile").ToList();
            if (tiles.Count == 0) break;

            foreach (var tile in tiles)
            {
                var product = ParseTile(tile);
                if (product is null) continue;
                if (seen.Add(product.ExternalId))
                {
                    products.Add(product);
                    scraped++;
                }
            }

            if (total is null)
            {
                var countEl = document.QuerySelector("[class*='result-count'], [class*='results-hits'], [class*='total-items']");
                total = ParseCount(countEl?.TextContent);
            }

            page++;

            if (scraped < (total ?? int.MaxValue))
                await Task.Delay(500, ct);

        } while (scraped < (total ?? 0) + PageSize);
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
        // "0.86 €/Lt" → 0.86
        var part = text.Split('€')[0].Trim().Replace(",", ".").Replace(" ", "");
        return decimal.TryParse(part, NumberStyles.Any, CultureInfo.InvariantCulture, out var val) && val > 0
            ? val : null;
    }

    private static int? ParseCount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = CountDigits().Match(text);
        return match.Success ? int.Parse(match.Value) : null;
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex CountDigits();
}
