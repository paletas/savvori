namespace Savvori.Shared;

public enum ModelJobType { Embed, Judge }

public enum ModelJobStatus
{
    Pending,
    Running,
    Done,
    /// <summary>Gave up after the maximum number of attempts; an admin can requeue it.</summary>
    DeadLetter
}

/// <summary>
/// One unit of model-backed background work (durable queue row). Idempotent: at most one
/// active (Pending/Running) row exists per (Type, SubjectId, PayloadHash).
/// </summary>
public class ModelJob
{
    public Guid Id { get; set; }
    public ModelJobType Type { get; set; }
    /// <summary>The entity the job is about (e.g. a StoreProduct id, or a candidate pair id).</summary>
    public Guid SubjectId { get; set; }
    /// <summary>Hash of the input the job was created for; a changed input is a different job.</summary>
    public string PayloadHash { get; set; } = string.Empty;
    public ModelJobStatus Status { get; set; } = ModelJobStatus.Pending;
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; }
    /// <summary>While Running, the time after which the claim is considered abandoned (e.g. restart mid-batch).</summary>
    public DateTime? LeaseExpiresAt { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
