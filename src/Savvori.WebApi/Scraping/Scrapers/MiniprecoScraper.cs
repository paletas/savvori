using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Savvori.Shared;

namespace Savvori.WebApi.Scraping.Scrapers;

/// <summary>
/// Scraper for Minipreço Portugal (minipreco.pt).
/// Uses SAP Hybris platform. Products are in .product-list__item elements.
/// Search URL: https://www.minipreco.pt/search?q={query}&page={page}
/// </summary>
public sealed partial class MiniprecoScraper : BaseHttpScraper
{
    public override string StoreChainSlug => "minipreco";

    private const string SearchBase = "https://www.minipreco.pt/search";
    private const string ProductBase = "https://www.minipreco.pt";
    private const int PageSize = 24;

    private static readonly string[] GrocerySearchTerms =
    [
        "leite", "iogurte", "queijo", "ovos", "carne", "peixe",
        "fruta", "legumes", "mercearia", "bebidas", "congelados",
        "padaria", "arroz", "massa", "azeite"
    ];

    public MiniprecoScraper(
        IHttpClientFactory httpClientFactory,
        ILogger<MiniprecoScraper> logger)
        : base(httpClientFactory, logger, "minipreco")
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

        Logger.LogInformation("Minipreço: scraped {Count} distinct products", products.Count);
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
        int? lastCount = null;

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
                Logger.LogError(ex, "Minipreço: failed to fetch query='{Query}' page={Page}", query, page);
                break;
            }

            var items = document.QuerySelectorAll(".product-list__item").ToList();
            if (items.Count == 0) break;
            if (items.Count == lastCount) break; // no progress, stop

            lastCount = items.Count;

            foreach (var item in items)
            {
                var product = ParseItem(item);
                if (product is null) continue;
                if (seen.Add(product.ExternalId))
                    products.Add(product);
            }

            // Minipreço shows a fixed grid per page; if we got fewer than PageSize, we're done
            if (items.Count < PageSize) break;

            page++;
            await Task.Delay(500, ct);

        } while (true);
    }

    private static ScrapedProduct? ParseItem(IElement item)
    {
        // Get product link — contains product ID and category path
        var linkEl = item.QuerySelector("a[href*='/p/']");
        if (linkEl is null) return null;

        var href = linkEl.GetAttribute("href") ?? string.Empty;
        var pid = ExtractProductId(href);
        if (string.IsNullOrEmpty(pid)) return null;

        // Extract raw link text for name parsing (strip trailing price)
        var linkText = linkEl.TextContent?.Trim() ?? string.Empty;
        var name = ExtractNameFromLinkText(linkText);
        if (string.IsNullOrEmpty(name)) return null;

        // Price from .price element
        var priceText = item.QuerySelector("p.price")?.TextContent?.Trim();
        var price = ParsePortugueseDecimal(priceText?.Replace("€", "").Trim());
        if (price is null || price <= 0) return null;

        // Unit price from .pricePerKilogram: "(1,14 €/l.)" → 1.14
        var unitPriceText = item.QuerySelector(".pricePerKilogram")?.TextContent?.Trim();
        var (unitPrice, unit) = ParseUnitPriceAndUnit(unitPriceText);

        // Promotion detection: badge with non-empty text like "-12% SOBRE PVPR"
        var badgeEl = item.QuerySelector("[class*='badge'], [class*='promo'], [class*='discount'], [class*='offer']");
        var promoDesc = badgeEl?.TextContent?.Trim();
        var isPromo = !string.IsNullOrEmpty(promoDesc);

        // Image
        var imgEl = item.QuerySelector("img[src]");
        var imageUrl = imgEl?.GetAttribute("src");

        // Source URL
        var sourceUrl = href.StartsWith("http") ? href : $"{ProductBase}{href}";

        // Category from URL path: /produtos/{category1}/{category2}/{leaf}/p/{id}
        var category = ExtractCategoryFromUrl(href);

        // Size from product name
        var sizeUnit = ProductNormalizer.ExtractSizeAndUnit(name);

        return new ScrapedProduct(
            Name: name,
            Brand: null, // Minipreço tiles don't expose brand separately
            Category: category,
            Price: price.Value,
            UnitPrice: unitPrice,
            EAN: null,
            ExternalId: pid,
            ImageUrl: imageUrl,
            SourceUrl: sourceUrl,
            IsPromotion: isPromo,
            PromotionDescription: promoDesc,
            Unit: unit ?? sizeUnit?.Unit ?? ProductUnit.Unit,
            SizeValue: sizeUnit?.SizeValue
        );
    }

    private static string? ExtractProductId(string href)
    {
        var match = ProductIdPattern().Match(href);
        return match.Success ? match.Groups["id"].Value : null;
    }

    private static string ExtractNameFromLinkText(string text)
    {
        // Link text includes name + price, e.g. "MIMOSA Leite Magro 1 L\n\n1,14 €\n\n(1,14 €/l.)"
        // Take everything before the first price-like sequence (decimal + €)
        var priceIndex = PriceInText().Match(text).Index;
        return (priceIndex > 0 ? text[..priceIndex] : text).Trim();
    }

    private static string? ExtractCategoryFromUrl(string href)
    {
        // /produtos/{cat1}/{cat2}/{leaf}/p/{id} → leaf
        var parts = href.TrimStart('/').Split('/');
        var pIdx = Array.IndexOf(parts, "p");
        if (pIdx > 1) return parts[pIdx - 1];
        return parts.Length > 2 ? parts[^2] : null;
    }

    private static decimal? ParsePortugueseDecimal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return decimal.TryParse(
            text.Replace(",", ".").Replace(" ", ""),
            NumberStyles.Any, CultureInfo.InvariantCulture,
            out var val) ? val : null;
    }

    private static (decimal? UnitPrice, ProductUnit? Unit) ParseUnitPriceAndUnit(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);

        // "(1,14 €/l.)" or "(19,10 €/Kg.)"
        var match = UnitPricePattern().Match(text);
        if (!match.Success) return (null, null);

        var val = ParsePortugueseDecimal(match.Groups["price"].Value);
        var rawUnit = match.Groups["unit"].Value.ToLowerInvariant().TrimEnd('.');

        var unit = rawUnit switch
        {
            "kg" => ProductUnit.Kg,
            "g" or "gr" => ProductUnit.G,
            "l" => ProductUnit.L,
            "ml" => ProductUnit.Ml,
            _ => ProductUnit.Unit
        };

        return (val > 0 ? val : null, unit);
    }

    [GeneratedRegex(@"/p/(?<id>\d+)")]
    private static partial Regex ProductIdPattern();

    // Matches price at the end of link text like "1,14 €"
    [GeneratedRegex(@"\d[\d,]*\s*€")]
    private static partial Regex PriceInText();

    // Matches "(1,14 €/l.)" or "(19,10 €/Kg.)"
    [GeneratedRegex(@"\((?<price>[\d,]+)\s*€/(?<unit>[a-zA-Z.]+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex UnitPricePattern();
}
