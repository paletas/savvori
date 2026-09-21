namespace Savvori.Shared;

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
}
