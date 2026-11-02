using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Savvori.Shared;

namespace Savvori.WebApi.Scraping.Scrapers;

/// <summary>
/// Scraper for Lidl Portugal (lidl.pt) via the JSON search API the storefront itself uses.
/// Endpoint: /q/api/search?q={query}&amp;offset={n}&amp;fetchsize=48&amp;locale=pt_PT&amp;assortment=PT&amp;version=2.1.0
/// An empty query lists the whole online assortment (food and non-food, <c>numFound</c> items, 48 per page);
/// every priced item is kept and the listing is paged until <c>numFound</c> via <see cref="PagingGuard"/>.
/// Prices are Lidl's published prices, most items are in-store only.
/// Note: the API answers 406 to a strict "Accept: application/json".
/// </summary>
public sealed partial class LidlScraper : BaseHttpScraper
{
    public override string StoreChainSlug => "lidl";

    private const string BaseUrl = "https://www.lidl.pt";
    private const int FetchSize = 48;

    public LidlScraper(
        IHttpClientFactory httpClientFactory,
        ILogger<LidlScraper> logger)
        : base(httpClientFactory, logger, "lidl")
    {
        RequestCeiling = 15;
    }

    public override async Task<ScrapeRunResult> ScrapeProductsAsync(
        string? category = null, CancellationToken ct = default)
    {
        BeginRun();
        var warnings = new List<string>();
        var seen = new HashSet<string>();
        var products = new List<ScrapedProduct>();
        var outcomes = new List<ScrapeOutcome>();
        string[] queries = category is not null ? [category] : [""];

        SetKeysTotal(queries.Length);
        foreach (var query in queries)
        {
            if (Halted) break;
            ct.ThrowIfCancellationRequested();
            await ScrapeQuery(query, seen, products, outcomes, warnings, ct);
            ReportKeyDone(query.Length == 0 ? "(empty)" : query, products.Count);
        }

        await ReattemptAsync(outcomes, async old =>
        {
            var tmp = new List<ScrapeOutcome>();
            await ScrapeQuery(old.Key == "(empty)" ? "" : old.Key, seen, products, tmp, warnings, ct);
            return tmp[0];
        }, ct);

        Logger.LogInformation("Lidl: scraped {Count} distinct products", products.Count);
        return BuildResult(products, outcomes, warnings);
    }

    public override Task<CanaryResult> ProbeAsync(CancellationToken ct = default)
        => ProbeStringAsync(SearchUrl("", 0), body =>
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return (0, 0);
            return (items.GetArrayLength(), items.EnumerateArray().Count(HasCoreFields));
        }, ct);

    private async Task ScrapeQuery(
        string query, HashSet<string> seen, List<ScrapedProduct> products,
        List<ScrapeOutcome> outcomes, List<string> warnings, CancellationToken ct)
    {
        var outcome = new ScrapeOutcome
        {
            Kind = OutcomeKind.Term,
            Key = query.Length == 0 ? "(empty)" : query,
            Url = SearchUrl(query, 0)
        };
        outcomes.Add(outcome);
        PagingGuard? guard = null;
        for (var offset = 0; ; offset += FetchSize)
        {
            if (Halted) { outcome.StopReason ??= $"run halted: {HaltReason}"; return; }
            var url = SearchUrl(query, offset);

            JsonDocument doc;
            var body = await GetStringAsync(url, outcome, ct);
            if (body is null)
            {
                Logger.LogError("Lidl: failed to fetch query='{Query}' offset={Offset}: {Err}",
                    query, offset, outcome.FirstError);
                return;
            }
            try
            {
                doc = JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                if (outcome.Result == OutcomeResult.Ok) outcome.Result = OutcomeResult.Failed;
                outcome.FirstError ??= ex.Message;
                outcome.StopReason ??= $"failed: {ex.Message}";
                Logger.LogError(ex, "Lidl: invalid JSON for query='{Query}' offset={Offset}", query, offset);
                return;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                {
                    if (offset == 0) MarkFirstPage(outcome, 0, 0);
                    outcome.StopReason = "no items array in response";
                    return;
                }

                var numFound = root.TryGetProperty("numFound", out var nf) && nf.TryGetInt32(out var n) ? n : 0;
                var ids = items.EnumerateArray().Where(HasCoreFields)
                    .Select(i => i.GetProperty("gridbox").GetProperty("data").GetProperty("itemId").ToString()).ToList();

                if (offset == 0)
                {
                    MarkFirstPage(outcome, items.GetArrayLength(), ids.Count);
                    outcome.SiteTotal = numFound > 0 ? numFound : null;
                }
                outcome.ProductsListed += ids.Count;

                foreach (var item in items.EnumerateArray())
                {
                    var product = ParseItem(item);
                    if (product is not null && seen.Add(product.ExternalId))
                    {
                        products.Add(product);
                        outcome.ProductsFound++;
                    }
                }

                ReportProgress(outcome.Key, offset, products.Count, outcome.SiteTotal, FetchSize);
                guard ??= new PagingGuard(outcome.Key, FetchSize, numFound > 0 ? numFound : null);
                var verdict = guard.Observe(ids, offset, items.GetArrayLength());
                outcome.StopReason = guard.StopReason;
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
    }

    public override Task<IReadOnlyList<ScrapedStoreLocation>> ScrapeStoreLocationsAsync(
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ScrapedStoreLocation>>([]);
    }

    private static string SearchUrl(string query, int offset)
        => $"{BaseUrl}/q/api/search?q={Uri.EscapeDataString(query)}&offset={offset}" +
           $"&fetchsize={FetchSize}&locale=pt_PT&assortment=PT&version=2.1.0";

    /// <summary>True when the item carries name, price and id, regardless of its category.</summary>
    private static bool HasCoreFields(JsonElement item)
    {
        if (!item.TryGetProperty("gridbox", out var gridbox) ||
            !gridbox.TryGetProperty("data", out var data))
            return false;
        var id = data.TryGetProperty("itemId", out var idEl) ? idEl.ToString() : null;
        var name = GetString(data, "fullTitle") ?? GetString(data, "title");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) return false;
        return data.TryGetProperty("price", out var priceEl) &&
               priceEl.TryGetProperty("price", out var pv) && pv.TryGetDecimal(out var price) && price > 0;
    }

    private static ScrapedProduct? ParseItem(JsonElement item)
    {
        if (!item.TryGetProperty("gridbox", out var gridbox) ||
            !gridbox.TryGetProperty("data", out var data))
            return null;

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
