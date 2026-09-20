using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Savvori.Shared;

namespace Savvori.WebApi.Scraping.Scrapers;

/// <summary>
/// Scraper for Celeiro (celeiro.pt), a Portuguese organic / health-food chain.
/// Magento storefront; product tiles are .product-item-info with schema.org microdata.
/// Category URL: https://www.celeiro.pt/produtos/{category}?p={page}
/// Search URL:   https://www.celeiro.pt/catalogsearch/result/?q={query}&amp;p={page}
/// Magento serves the last page again for out-of-range page numbers, so paging stops
/// when a page yields no new products.
/// </summary>
public sealed partial class CeleiroScraper : BaseHttpScraper
{
    public override string StoreChainSlug => "celeiro";

    private const string BaseUrl = "https://www.celeiro.pt";
    private const int MaxPagesPerListing = 60;

    // Food-related top-level categories (skips cosmetics, supplements and eco-recycling).
    private static readonly string[] GroceryCategories = ["alimentacao", "biologico"];

    public CeleiroScraper(
        IHttpClientFactory httpClientFactory,
        ILogger<CeleiroScraper> logger)
        : base(httpClientFactory, logger, "celeiro")
    {
    }

    public override async Task<IReadOnlyList<ScrapedProduct>> ScrapeProductsAsync(
        string? category = null, CancellationToken ct = default)
    {
        var seen = new HashSet<string>();
        var products = new List<ScrapedProduct>();

        if (category is not null)
        {
            await ScrapeListing(
                p => $"{BaseUrl}/catalogsearch/result/?q={Uri.EscapeDataString(category)}&p={p}",
                null, seen, products, ct);
        }
        else
        {
            foreach (var slug in GroceryCategories)
            {
                ct.ThrowIfCancellationRequested();
                await ScrapeListing(p => $"{BaseUrl}/produtos/{slug}?p={p}", slug, seen, products, ct);
            }
        }

        Logger.LogInformation("Celeiro: scraped {Count} distinct products", products.Count);
        return products;
    }

    public override Task<IReadOnlyList<ScrapedStoreLocation>> ScrapeStoreLocationsAsync(
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ScrapedStoreLocation>>([]);
    }

    private async Task ScrapeListing(
        Func<int, string> urlForPage,
        string? category,
        HashSet<string> seen,
        List<ScrapedProduct> products,
        CancellationToken ct)
    {
        for (var page = 1; page <= MaxPagesPerListing; page++)
        {
            IDocument document;
            try
            {
                document = await GetHtmlAsync(urlForPage(page), ct);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Celeiro: failed to fetch {Url}", urlForPage(page));
                return;
            }

            var items = document.QuerySelectorAll(".product-item-info").ToList();
            if (items.Count == 0) return;

            var added = 0;
            foreach (var item in items)
            {
                var product = ParseItem(item, category);
                if (product is not null && seen.Add(product.ExternalId))
                {
                    products.Add(product);
                    added++;
                }
            }

            if (added == 0) return; // Magento repeated the last page

            await Task.Delay(600, ct);
        }
    }

    private static ScrapedProduct? ParseItem(IElement item, string? category)
    {
        var sku = item.QuerySelector("[itemprop='sku']")?.GetAttribute("content");
        var linkEl = item.QuerySelector("a.product-item-link");
        if (string.IsNullOrWhiteSpace(sku) || linkEl is null) return null;

        var name = (linkEl.GetAttribute("title") ?? linkEl.TextContent).Trim();
        if (string.IsNullOrEmpty(name)) return null;

        var price = ParseAmount(item.QuerySelector("[data-price-type='finalPrice']")?.GetAttribute("data-price-amount"))
                    ?? ParseAmount(item.QuerySelector("[itemprop='price']")?.GetAttribute("content"));
        if (price is null || price <= 0) return null;

        var oldPrice = ParseAmount(item.QuerySelector("[data-price-type='oldPrice']")?.GetAttribute("data-price-amount"));
        var isPromo = oldPrice is not null && oldPrice > price;
        var promoDesc = isPromo ? $"Antes {oldPrice:0.00} €" : null;

        var brand = item.QuerySelector(".brand a")?.TextContent.Trim();

        // "380  ML  •  63.84€ por Ltr."
        var presentation = Regex.Replace(item.QuerySelector(".apresentacao")?.TextContent ?? "", @"\s+", " ").Trim();
        var (unitPrice, unit) = ParseUnitPrice(presentation);
        var sizeText = presentation.Split('•')[0].Trim();
        var sizeUnit = ProductNormalizer.ExtractSizeAndUnit($"{name} {sizeText}");

        var href = linkEl.GetAttribute("href") ?? string.Empty;
        var sourceUrl = href.StartsWith("http") ? href : $"{BaseUrl}/{href.TrimStart('/')}";

        return new ScrapedProduct(
            Name: name,
            Brand: string.IsNullOrEmpty(brand) ? null : brand,
            Category: category,
            Price: price.Value,
            UnitPrice: unitPrice,
            EAN: null,
            ExternalId: sku,
            ImageUrl: item.QuerySelector("[itemprop='image']")?.GetAttribute("content"),
            SourceUrl: sourceUrl,
            IsPromotion: isPromo,
            PromotionDescription: promoDesc,
            Unit: unit ?? sizeUnit?.Unit ?? ProductUnit.Unit,
            SizeValue: sizeUnit?.SizeValue
        );
    }

    private static decimal? ParseAmount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return decimal.TryParse(text.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
            ? v : null;
    }

    private static (decimal? UnitPrice, ProductUnit? Unit) ParseUnitPrice(string presentation)
    {
        var match = UnitPricePattern().Match(presentation);
        if (!match.Success) return (null, null);

        var value = ParseAmount(match.Groups["price"].Value);
        var unit = match.Groups["unit"].Value.ToLowerInvariant().TrimEnd('.') switch
        {
            "kg" => ProductUnit.Kg,
            "g" => ProductUnit.G,
            "ltr" or "l" => ProductUnit.L,
            "ml" => ProductUnit.Ml,
            _ => ProductUnit.Unit
        };

        return (value > 0 ? value : null, unit);
    }

    // "63.84€ por Ltr." / "23.20€ por Kg"
    [GeneratedRegex(@"(?<price>\d+(?:[.,]\d+)?)\s*€\s*por\s*(?<unit>[a-zA-Z.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex UnitPricePattern();
}
