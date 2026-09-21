namespace Savvori.Shared;

public enum BulkBatchStatus { Running, Done, Failed, Undoing, Undone }

/// <summary>
/// One bulk action from the review queues ("apply every confident suggestion above a threshold"), recorded as a single
/// run so the whole run can be undone in one step. The changes made carry this id (MatchMerge.BatchId,
/// CategorySuggestion.BatchId).
/// </summary>
public class BulkBatch
{
    public Guid Id { get; set; }
    /// <summary>"matches" or "categories".</summary>
    public string Kind { get; set; } = string.Empty;
    /// <summary>What was applied: "embedding-cosine" for matches, "embedding-knn" for categories.</summary>
    public string Method { get; set; } = string.Empty;
    /// <summary>The cut-off used: minimum cosine (matches) or minimum confidence (categories).</summary>
    public double Threshold { get; set; }
    public BulkBatchStatus Status { get; set; }
    /// <summary>Items eligible when the run started.</summary>
    public int Total { get; set; }
    public int Applied { get; set; }
    /// <summary>Items left in the queue because a safety rule blocked them (or they changed meanwhile).</summary>
    public int Blocked { get; set; }
    public int Undone { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public DateTime? UndoneAt { get; set; }
}
