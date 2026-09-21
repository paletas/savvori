namespace Savvori.Shared;

public enum CategorySuggestionStatus
{
    /// <summary>Waiting for a human (confidence in the review band, or a dry-run proposal).</summary>
    Suggested = 0,
    /// <summary>The product's category was set (see Method for who or what decided). Reversible via PreviousCategoryId.</summary>
    Applied = 1,
    /// <summary>Decided "not this category". Never proposed again for this product.</summary>
    Rejected = 2,
    /// <summary>Only for string decisions: the string is not homogeneous enough to decide as a whole; products go one by one.</summary>
    Mixed = 3
}

/// <summary>
/// A model-made (or human-confirmed) category decision for one canonical product, with everything needed to audit and
/// undo it. The classifier only ever considers products that have NO category, so it can never overwrite a label.
/// </summary>
public class CategorySuggestion
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public Guid SuggestedCategoryId { get; set; }
    public ProductCategory SuggestedCategory { get; set; } = null!;
    public Guid? RunnerUpCategoryId { get; set; }
    /// <summary>Similarity-weighted share of the winning category among the nearest labelled neighbours (0..1).</summary>
    public double Confidence { get; set; }
    public int NeighbourCount { get; set; }
    public CategorySuggestionStatus Status { get; set; }
    /// <summary>"embedding-knn", "string-cache" or "manual-review".</summary>
    public string Method { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string ModelDigest { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    /// <summary>The product's category before this decision was applied (null: it had none), used to undo.</summary>
    public Guid? PreviousCategoryId { get; set; }
    public string? Note { get; set; }
}

/// <summary>
/// The decision for one raw store-category string (for example a store's "alimentacao"), cached so it is made once
/// per string rather than once per product.
/// </summary>
public class CategoryStringDecision
{
    public Guid Id { get; set; }
    /// <summary>Normalised raw string (accent-free, lower-case).</summary>
    public string RawString { get; set; } = string.Empty;
    public Guid? CategoryId { get; set; }
    public ProductCategory? Category { get; set; }
    public double Confidence { get; set; }
    /// <summary>How many uncategorised products carried the string when it was decided.</summary>
    public int Support { get; set; }
    public CategorySuggestionStatus Status { get; set; }
    public string Method { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string ModelDigest { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
}
