namespace Savvori.Shared;

/// <summary>Answer of the pair judge (mirrors the WebApi type so it can be stored).</summary>
public enum JudgeVerdict { Yes, No, Unclear }

/// <summary>
/// The embedding of one chain's listing text. Stored per StoreProduct (not per canonical) because the text
/// embedded is that chain's own listing and canonicals can be merged/split. Every vector records exactly
/// which model and which input produced it, so it can be recognised as stale and never compared across models.
/// </summary>
public class StoreProductEmbedding
{
    public Guid StoreProductId { get; set; }
    public StoreProduct StoreProduct { get; set; } = null!;
    /// <summary>float32 little-endian, <see cref="Dimension"/> values, as returned by the model (not normalised).</summary>
    public byte[] Vector { get; set; } = [];
    public string ModelName { get; set; } = string.Empty;
    public string ModelDigest { get; set; } = string.Empty;
    public int Dimension { get; set; }
    /// <summary>SHA-256 of the exact text that was embedded.</summary>
    public string InputTextHash { get; set; } = string.Empty;
    /// <summary>Last time the vector was (re)computed; drives the incremental index refresh.</summary>
    public DateTime EmbeddedAt { get; set; }
}

public enum CandidateBrandCheck
{
    /// <summary>Both brands known and compatible, or the known brand appears in the other listing's name.</summary>
    Ok,
    /// <summary>At least one brand is missing and could not be inferred from the other listing.</summary>
    Unknown
}

public enum CandidateStatus
{
    /// <summary>Generated, no decision yet.</summary>
    Proposed = 0,
    /// <summary>Waiting for the pair judge (queued; a failed call leaves it here and is retried).</summary>
    PendingJudge = 1,
    /// <summary>Needs a human: uncertain, judge unclear/no, blocked by a safety rule, or a dry-run proposal.</summary>
    NeedsReview = 2,
    /// <summary>The two products were linked to one canonical (see MatchMerge for how to undo).</summary>
    Applied = 3,
    /// <summary>Decided "not the same product". Never proposed again.</summary>
    Rejected = 4,
    /// <summary>Decided "same family but a different variant". Never proposed again.</summary>
    DifferentVariant = 5
}

/// <summary>
/// A proposed cross-chain match between two StoreProducts, produced from embedding similarity.
/// A proposal only: nothing is linked or merged by generating one. The pair is stored with A &lt; B (by id).
/// </summary>
public class MatchCandidate
{
    public Guid Id { get; set; }
    public Guid StoreProductAId { get; set; }
    public StoreProduct StoreProductA { get; set; } = null!;
    public Guid StoreProductBId { get; set; }
    public StoreProduct StoreProductB { get; set; } = null!;
    public double Cosine { get; set; }
    /// <summary>False when either size is unknown; such pairs need stricter thresholds downstream.</summary>
    public bool SizeKnown { get; set; }
    public CandidateBrandCheck BrandCheck { get; set; }
    public string ModelName { get; set; } = string.Empty;
    public string ModelDigest { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    // --- decision (Phase 3): every model-made decision stays attributable and reversible ---
    public CandidateStatus Status { get; set; } = CandidateStatus.Proposed;
    /// <summary>How the current status was reached: "embedding-cosine", "embedding-judge" or "manual".</summary>
    public string? Method { get; set; }
    /// <summary>Dry run: what an applying run would have done ("embedding-cosine" / "embedding-judge").</summary>
    public string? Suggestion { get; set; }
    public JudgeVerdict? JudgeVerdict { get; set; }
    public string? JudgeModel { get; set; }
    public DateTime? DecidedAt { get; set; }
    /// <summary>Human-readable reason, e.g. why a merge was blocked.</summary>
    public string? Note { get; set; }
}

/// <summary>Audit record of one canonical merge, sufficient to undo it.</summary>
public class MatchMerge
{
    public Guid Id { get; set; }
    public Guid CandidateId { get; set; }
    /// <summary>The canonical that survived and now owns all the store products.</summary>
    public Guid SurvivorProductId { get; set; }
    /// <summary>The canonical that was retired (deleted); null when a store product was simply linked to an existing canonical.</summary>
    public Guid? RetiredProductId { get; set; }
    /// <summary>JSON snapshot of the retired canonical, used to recreate it on undo.</summary>
    public string? RetiredProductJson { get; set; }
    public Guid? SurvivorPreviousCategoryId { get; set; }
    /// <summary>JSON list of moved store products with their previous canonical and match fields.</summary>
    public string MovedStoreProductsJson { get; set; } = "[]";
    /// <summary>JSON list of shopping list item ids that were redirected from the retired canonical.</summary>
    public string MovedListItemsJson { get; set; } = "[]";
    public string Method { get; set; } = string.Empty;
    public DateTime AppliedAt { get; set; }
    public DateTime? UndoneAt { get; set; }
}
