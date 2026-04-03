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

    private static readonly Regex SizePattern = SizeRegex();
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
    /// Attempts to extract size (e.g., 1.5 from "1.5L") and unit from a product name.
    /// Returns null if not found.
    /// </summary>
    public static (decimal SizeValue, ProductUnit Unit)? ExtractSizeAndUnit(string name)
    {
        var match = SizePattern.Match(name);
        if (!match.Success) return null;

        if (!decimal.TryParse(match.Groups["value"].Value,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var value))
            return null;

        var rawUnit = match.Groups["unit"].Value.ToLowerInvariant().Trim();
        var unit = rawUnit switch
        {
            "kg" => ProductUnit.Kg,
            "g" or "gr" or "grs" => ProductUnit.G,
            "l" or "lt" or "lts" or "litro" or "litros" => ProductUnit.L,
            "ml" or "cl" => rawUnit == "cl" ? ProductUnit.Ml : ProductUnit.Ml,
            "un" or "uni" or "unid" or "unidade" or "unidades" => ProductUnit.Unit,
            "pack" or "pck" => ProductUnit.Pack,
            _ => ProductUnit.Unit
        };

        // Convert cl to ml
        if (rawUnit == "cl") value *= 10;

        return (value, unit);
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
        @"(?<value>\d+[.,]?\d*)\s*(?<unit>kg|gr?s?|ml|cl|l|lt|lts?|litros?|un|uni|unid\w*|pack|pck)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[^a-z0-9\s]")]
    private static partial Regex NonAlphanumericRegex();
}
