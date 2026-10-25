namespace Savvori.Shared;

public enum ModelCallStatus { Ok, Throttled, Unavailable, Error }

/// <summary>
/// One model HTTP request: who, what, how long, tokens and cost. Never holds request or response
/// bodies; pruned after 90 days.
/// </summary>
public class ModelCall
{
    public Guid Id { get; set; }
    /// <summary>UTC time of the request.</summary>
    public DateTime At { get; set; }
    /// <summary>gateway | openrouter.</summary>
    public string Backend { get; set; } = string.Empty;
    /// <summary>embed | category | translate | pair | probe.</summary>
    public string Task { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public ModelCallStatus Status { get; set; }
    public int? HttpStatus { get; set; }
    public int LatencyMs { get; set; }
    public int Items { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public decimal? CostUsd { get; set; }
}
