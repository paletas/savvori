using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Savvori.Shared;

namespace Savvori.WebApi.Scraping.Scrapers;

public sealed partial class ContinenteScraper : BaseHttpScraper
{
    public override string StoreChainSlug => "continente";

    private const string BaseUrl = "https://www.continente.pt";
    private const int PageSize = 48;

    // Grocery-focused top-level category slugs
    private static readonly string[] GroceryCategories =
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
        var categories = category is not null
            ? [category]
            : GroceryCategories;

        var seen = new HashSet<string>();
        var products = new List<ScrapedProduct>();

        foreach (var cat in categories)
        {
            ct.ThrowIfCancellationRequested();
            await ScrapeCategory(cat, seen, products, ct);
            await Task.Delay(800, ct);
        }

        Logger.LogInformation("Continente: scraped {Count} distinct products", products.Count);
        return products;
    }

    public override Task<IReadOnlyList<ScrapedStoreLocation>> ScrapeStoreLocationsAsync(
        CancellationToken ct = default)
    {
        // Store location scraping would require separate implementation
        return Task.FromResult<IReadOnlyList<ScrapedStoreLocation>>([]);
    }

    private async Task ScrapeCategory(
        string categorySlug,
        HashSet<string> seen,
        List<ScrapedProduct> products,
        CancellationToken ct)
    {
        var start = 0;
        int? total = null;

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

            foreach (var tile in tiles)
            {
                var product = ParseTile(tile);
                if (product is null) continue;
                if (seen.Add(product.ExternalId))
                    products.Add(product);
            }

            if (total is null)
            {
                var countText = document.QuerySelector(".ct-results-count, .results-hits")?.TextContent;
                total = ParseCount(countText);
            }

            start += PageSize;

            if (start < (total ?? int.MaxValue))
                await Task.Delay(500, ct);

        } while (start < (total ?? 0) + PageSize);
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
        return match.Success ? int.Parse(match.Value) : null;
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex CountDigits();
}
