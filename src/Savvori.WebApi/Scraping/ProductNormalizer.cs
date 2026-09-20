using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Savvori.Shared;

namespace Savvori.WebApi.Scraping;

/// <summary>
/// Utilities for normalizing product names and extracting structured data.
/// </summary>
public static partial class ProductNormalizer
{
    private static readonly UnicodeCategory[] NonSpacingMarkCategories =
        [UnicodeCategory.NonSpacingMark];

    private const decimal SizeTolerance = 0.05m;

    private static readonly Regex SizePattern = SizeRegex();
    private static readonly Regex PackCountPattern = PackCountRegex();
    private static readonly Regex WhitespacePattern = WhitespaceRegex();
    private static readonly Regex NonAlphanumericPattern = NonAlphanumericRegex();

    /// <summary>
    /// Produces a normalized, accent-free, lowercase key used for cross-store product matching.
    /// </summary>
    public static string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var normalized = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (!NonSpacingMarkCategories.Contains(CharUnicodeInfo.GetUnicodeCategory(c)))
                sb.Append(c);
        }

        var result = sb.ToString()
            .Normalize(NormalizationForm.FormC)
            .ToLowerInvariant();

        result = NonAlphanumericPattern.Replace(result, " ");
        result = WhitespacePattern.Replace(result, " ").Trim();
        return result;
    }

    /// <summary>
    /// Attempts to extract size (e.g., 1.5 from "1,5L") and unit from a product name.
    /// Accepts decimal commas or points. Multipacks ("6x33cl", "Pack 6 Latas 33cl") return the
    /// total quantity (1980 ml); a bare "Pack 6" returns (6, Pack). Returns null if not found.
    /// </summary>
    public static (decimal SizeValue, ProductUnit Unit)? ExtractSizeAndUnit(string name)
    {
        var match = SizePattern.Match(name);
        if (!match.Success)
        {
            var bare = PackCountPattern.Match(name);
            return bare.Success && int.TryParse(bare.Groups["count"].Value, out var packCount) && packCount > 0
                ? (packCount, ProductUnit.Pack)
                : null;
        }

        if (!TryParseDecimal(match.Groups["value"].Value, out var value))
            return null;

        var rawUnit = match.Groups["unit"].Value.ToLowerInvariant().Trim();
        var unit = rawUnit switch
        {
            "kg" => ProductUnit.Kg,
            "g" or "gr" or "grs" => ProductUnit.G,
            "l" or "lt" or "lts" or "litro" or "litros" => ProductUnit.L,
            "ml" or "cl" => ProductUnit.Ml,
            "un" or "uni" or "unid" or "unidade" or "unidades" => ProductUnit.Unit,
            "pack" or "pck" => ProductUnit.Pack,
            _ => ProductUnit.Unit
        };

        // Convert cl to ml
        if (rawUnit == "cl") value *= 10;

        // Multipack: "6x33cl" or "Pack 6 ... 33cl" → total quantity
        if (match.Groups["count"].Success && int.TryParse(match.Groups["count"].Value, out var count) && count > 0)
            value *= count;
        else if (unit is ProductUnit.Kg or ProductUnit.G or ProductUnit.L or ProductUnit.Ml)
        {
            var pack = PackCountPattern.Match(name);
            if (pack.Success && int.TryParse(pack.Groups["count"].Value, out var packCount) && packCount > 1)
                value *= packCount;
        }

        return (value, unit);
    }

    /// <summary>
    /// Parses a decimal that may use a comma ("0,5") or a point ("0.5") as separator.
    /// </summary>
    public static bool TryParseDecimal(string? text, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return decimal.TryParse(text.Trim().Replace(',', '.'),
            NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Cross-checks a parsed size against the store's own unit price (per kg / per litre).
    /// If the size implied by <c>price ÷ unitPrice</c> differs from the parsed size by more than 5%,
    /// the implied size wins and <c>Disagreed</c> is true. Count-based units and missing data are
    /// returned unchanged.
    /// </summary>
    public static (decimal? SizeValue, ProductUnit Unit, bool Disagreed) ReconcileSizeWithUnitPrice(
        decimal price, decimal? unitPrice, decimal? sizeValue, ProductUnit unit)
    {
        if (sizeValue is null or <= 0 || unitPrice is null or <= 0 || price <= 0 ||
            unit is not (ProductUnit.Kg or ProductUnit.G or ProductUnit.L or ProductUnit.Ml))
            return (sizeValue, unit, false);

        // Sizes in kg / L
        var scale = unit is ProductUnit.G or ProductUnit.Ml ? 1000m : 1m;
        var implied = price / unitPrice.Value;
        var parsed = sizeValue.Value / scale;

        if (Math.Abs(parsed - implied) / implied <= SizeTolerance)
            return (sizeValue, unit, false);

        return (SnapToRoundSize(implied * scale), unit, true);
    }

    // Unit prices are rounded to cents, so the implied size is noisy (0.5042 for a true 0.5).
    // Use the coarsest 1–3 significant-figure value within 1% of it.
    private static decimal SnapToRoundSize(decimal implied)
    {
        for (var digits = 1; digits <= 3; digits++)
        {
            var magnitude = (int)Math.Floor(Math.Log10((double)implied)) + 1;
            var decimals = digits - magnitude;
            var rounded = decimals >= 0
                ? Math.Round(implied, Math.Min(decimals, 28), MidpointRounding.AwayFromZero)
                : Math.Round(implied / (decimal)Math.Pow(10, -decimals), MidpointRounding.AwayFromZero)
                  * (decimal)Math.Pow(10, -decimals);
            if (rounded > 0 && Math.Abs(rounded - implied) / implied <= 0.01m)
                return rounded;
        }
        return Math.Round(implied, 3);
    }

    /// <summary>
    /// Computes a canonical unit price (per kg or per litre) for comparison.
    /// Returns null if conversion is not applicable.
    /// </summary>
    public static decimal? ComputeUnitPrice(decimal price, ProductUnit unit, decimal? sizeValue)
    {
        if (sizeValue is null or <= 0) return null;
        return unit switch
        {
            ProductUnit.Kg => price / sizeValue.Value,
            ProductUnit.G => price / (sizeValue.Value / 1000m),
            ProductUnit.L => price / sizeValue.Value,
            ProductUnit.Ml => price / (sizeValue.Value / 1000m),
            _ => null
        };
    }

    [GeneratedRegex(
        @"(?<![\d.,])(?:(?<count>\d+)\s*[x×]\s*)?(?<value>\d+(?:[.,]\d+)?)\s*(?<unit>kg|gr?s?|ml|cl|l|lt|lts|litros?|un|uni|unid\w*|pack|pck)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"\bpack\s*(?:de\s*)?(?<count>\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PackCountRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[^a-z0-9\s]")]
    private static partial Regex NonAlphanumericRegex();
}
