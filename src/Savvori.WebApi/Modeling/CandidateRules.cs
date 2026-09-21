using Savvori.Shared;
using Savvori.WebApi.Scraping;

namespace Savvori.WebApi.Modeling;

/// <summary>Everything the hard filters need to know about one listing.</summary>
public sealed record ListingFacts(
    Guid Id, Guid ChainId, Guid? CanonicalProductId, string Name, string? Brand, decimal? SizeValue, ProductUnit Unit);

public enum SizeVerdict { Compatible, Unknown, Conflict }
public enum BrandVerdict { Ok, Unknown, Conflict }

/// <summary>Pure hard filters applied to a nearest-neighbour pair before it becomes a candidate.</summary>
public static class CandidateRules
{
    private enum UnitClass { Mass, Volume, Count }

    private static (UnitClass Class, decimal Base) ToBase(decimal size, ProductUnit unit) => unit switch
    {
        ProductUnit.Kg => (UnitClass.Mass, size * 1000m),
        ProductUnit.G => (UnitClass.Mass, size),
        ProductUnit.L => (UnitClass.Volume, size * 1000m),
        ProductUnit.Ml => (UnitClass.Volume, size),
        _ => (UnitClass.Count, size) // Unit, Pack
    };

    /// <summary>
    /// Both sizes known: same unit class and within <paramref name="tolerance"/> (relative) or it is a conflict.
    /// Either size missing/non-positive: Unknown (kept, but flagged).
    /// </summary>
    public static SizeVerdict CompareSizes(ListingFacts a, ListingFacts b, double tolerance)
    {
        if (a.SizeValue is not > 0 || b.SizeValue is not > 0) return SizeVerdict.Unknown;
        var (ca, va) = ToBase(a.SizeValue.Value, a.Unit);
        var (cb, vb) = ToBase(b.SizeValue.Value, b.Unit);
        if (ca != cb) return SizeVerdict.Conflict;
        var larger = Math.Max(va, vb);
        return (double)(Math.Abs(va - vb) / larger) <= tolerance ? SizeVerdict.Compatible : SizeVerdict.Conflict;
    }

    /// <summary>
    /// Dietary variants must agree: if one listing is sem lactose, sem glúten, vegan or sem açúcar and the other is not,
    /// they are different products ("Chocolate Negro" vs "Chocolate Negro sem Açúcar"). Deterministic tag rules on name
    /// and brand; a missed tag can only cause a missed match, never a wrong merge. "Bio" is deliberately NOT compared:
    /// chains label organic inconsistently (Celeiro, an organic chain, mostly does not say so), so it would reject
    /// genuine matches; it stays a tag only.
    /// </summary>
    public static bool TagsConflict(ListingFacts a, ListingFacts b)
    {
        var ta = TagRules.Compute(a.Name, a.Brand, null).Where(t => t != TagRules.Bio).ToHashSet();
        var tb = TagRules.Compute(b.Name, b.Brand, null).Where(t => t != TagRules.Bio).ToHashSet();
        return !ta.SetEquals(tb);
    }

    /// <summary>
    /// Both brands present: equal, or one's tokens a subset of the other's, is Ok; anything else is a Conflict.
    /// One missing: Ok if the other's brand appears in that listing's name, else Unknown. Both missing: Unknown.
    /// </summary>
    public static BrandVerdict CompareBrands(ListingFacts a, ListingFacts b)
    {
        var ta = Tokens(a.Brand);
        var tb = Tokens(b.Brand);
        if (ta.Count > 0 && tb.Count > 0)
            return ta.IsSubsetOf(tb) || tb.IsSubsetOf(ta) ? BrandVerdict.Ok : BrandVerdict.Conflict;

        if (ta.Count > 0 && tb.Count == 0) return ta.IsSubsetOf(Tokens(b.Name)) ? BrandVerdict.Ok : BrandVerdict.Unknown;
        if (tb.Count > 0 && ta.Count == 0) return tb.IsSubsetOf(Tokens(a.Name)) ? BrandVerdict.Ok : BrandVerdict.Unknown;
        return BrandVerdict.Unknown;
    }

    private static HashSet<string> Tokens(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : ProductNormalizer.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
}
