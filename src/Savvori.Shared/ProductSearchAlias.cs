namespace Savvori.Shared;

/// <summary>
/// How a product is called in one language: a generic name plus search keywords ("Arroz Agulha" -> en: rice, long grain rice).
/// Used only to widen product search. Never used for matching, merging or categorising, and the vendor
/// <see cref="Product.Name"/> stays the source of truth. One row per (product, language).
/// </summary>
public class ProductSearchAlias
{
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    /// <summary>Primary language subtag: pt, en, es or fr.</summary>
    public string Language { get; set; } = string.Empty;
    /// <summary>Generic name in this language (first keyword); empty when the model had nothing for the language.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>The generic name and keywords as suggested, comma separated, for display or review.</summary>
    public string Keywords { get; set; } = string.Empty;
    /// <summary>Accent-free, lower-case form of <see cref="Keywords"/> (space separated) that search matches against.</summary>
    public string SearchText { get; set; } = string.Empty;
    /// <summary><c>model</c> or <c>manual</c>. A manual row is never overwritten by the model.</summary>
    public string Source { get; set; } = "model";
    public string? ModelName { get; set; }
    /// <summary>Hash of the product text and prompt version the row was made from; null for manual rows.</summary>
    public string? InputHash { get; set; }
    public DateTime CreatedAt { get; set; }
}
