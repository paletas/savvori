namespace Savvori.Shared;

/// <summary>A tag on a canonical product (bio, sem-lactose, sem-gluten, vegan, sem-acucar). Set by deterministic rules only.</summary>
public class ProductTag
{
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public string Tag { get; set; } = string.Empty;
}

/// <summary>One application of the taxonomy v2 migration. The latest row without RevertedAt means v2 is active.</summary>
public class TaxonomyMigration
{
    public Guid Id { get; set; }
    public int Version { get; set; } = 2;
    public DateTime AppliedAt { get; set; }
    public DateTime? RevertedAt { get; set; }
    /// <summary>JSON summary of what the run did (counts per outcome).</summary>
    public string? Summary { get; set; }
}
