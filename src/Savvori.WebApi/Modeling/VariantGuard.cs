using System.Text.RegularExpressions;
using Savvori.WebApi.Scraping;

namespace Savvori.WebApi.Modeling;

/// <summary>
/// Result of comparing the words of two listings. <see cref="Conflict"/>: they name different variants (flavour,
/// cocoa percentage, "light" vs regular, ...). <see cref="Identical"/>: once brand, size and filler words are removed
/// nothing is left over on either side, so the names say the same thing.
/// </summary>
public sealed record VariantVerdict(bool Conflict, bool Identical, string? Detail,
    IReadOnlyList<string>? OnlyA = null, IReadOnlyList<string>? OnlyB = null)
{
    public static readonly VariantVerdict Same = new(false, true, null);
}

/// <summary>
/// Deterministic check for the mistake embeddings make most: two names that differ only in the flavour or variant
/// ("Água com Gás Ananás" vs "Água com Gás Limão", "Chocolate 70%" vs "85%") look almost identical to a text model.
/// It compares the words that are not shared. Both sides having words of their own is a conflict; so is either side
/// carrying a known variant marker (light, zero, proteína, infantil, ...) the other lacks. A word only one side has
/// and that is not a marker ("Superior", a producer name) is not a conflict, but it also stops the pair from being
/// <see cref="VariantVerdict.Identical"/>.
///
/// Tuned by hand on real beta data (see docs/MODEL_MATCHING_PLAN.md); it errs towards flagging, which only costs a
/// manual review, never a wrong merge.
/// </summary>
public static class VariantGuard
{
    private static readonly HashSet<string> Stop = new(
        ("de do da dos das e com a o os as em para por no na nos nas ao um uma sabor tipo bebida vegetal base pack " +
         "un uni kg g gr l lt ml cl lata garrafa emb embalagem bio biologico biologica congelado congelados congeladas " +
         "ultracongelado ultracongelados ultracongeladas fresco fresca fatiado fatiada fatias extra virgem iogurte snack snacks " +
         "recheio ano anos meses massa massinha caixa saqueta saquetas unidades unidade cubos").Split(' '));

    // Words that make a different product when only one listing has them.
    private static readonly HashSet<string> Markers = new(
        ("light zero proteina integral descafeinado intenso mini max maxi xl xxl kids infantil junior barista crescimento " +
         "stevia grosso branco negro picante suave forte amendoa avela coco chocolate pink pizza joy").Split(' ').Select(Stem));

    private static readonly Regex Sizes = new(@"\b\d+(?: \d+)?\s?(?:kg|g|gr|ml|cl|l|lt)\b|\b\d+\s?x\s?\d+(?: \d+)?\s?(?:kg|g|gr|ml|cl|l|lt)?\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Age = new(@"(\d)\s?a\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Negation = new(@"\bsem (\w+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Word = new(@"[a-z0-9_]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static VariantVerdict Compare(string nameA, string? brandA, string nameB, string? brandB)
    {
        var brandStemsA = BrandStems(brandA);
        var brandStemsB = BrandStems(brandB);
        var a = Tokens(nameA, brandStemsA);
        var b = Tokens(nameB, brandStemsB);
        // A brand word one listing carries in its name (the other has it as its Brand field) is not a difference.
        // (A variant marker is never treated as brand: some chains put it in the brand field, e.g. "Oatly Barista".)
        var onlyA = a.Except(b).Where(t => !brandStemsB.Contains(t) || Markers.Contains(t)).ToList();
        var onlyB = b.Except(a).Where(t => !brandStemsA.Contains(t) || Markers.Contains(t)).ToList();

        if (onlyA.Count == 0 && onlyB.Count == 0) return VariantVerdict.Same;
        var marker = onlyA.Concat(onlyB).FirstOrDefault(Markers.Contains);
        if ((onlyA.Count > 0 && onlyB.Count > 0) || marker is not null)
            return new(true, false, $"{string.Join(' ', onlyA)} vs {string.Join(' ', onlyB)}".Trim(), onlyA, onlyB);
        return new(false, false, null, onlyA, onlyB);
    }

    private static HashSet<string> BrandStems(string? brand) =>
        string.IsNullOrWhiteSpace(brand)
            ? []
            : Word.Matches(ProductNormalizer.Normalize(brand)).Select(m => Stem(m.Value)).ToHashSet();

    private static HashSet<string> Tokens(string name, HashSet<string> brandStems)
    {
        var text = ProductNormalizer.Normalize(name);
        text = Sizes.Replace(text, " ");
        text = Age.Replace(text, "$1");
        text = Negation.Replace(text, "sem_$1");
        var result = new HashSet<string>();
        foreach (Match m in Word.Matches(text))
        {
            if (Stop.Contains(m.Value)) continue;
            var stem = Stem(m.Value);
            if (!brandStems.Contains(stem) || Markers.Contains(stem)) result.Add(stem);
        }
        return result;
    }

    /// <summary>Crude plural and gender stem, enough to equate "mirtilo/mirtilos" and "fruta/fruto".</summary>
    private static string Stem(string word)
    {
        if (word.Length > 3 && word[^1] == 's') word = word[..^1];
        if (word.Length > 4 && word[^1] is 'a' or 'e' or 'o') word = word[..^1];
        return word;
    }
}
