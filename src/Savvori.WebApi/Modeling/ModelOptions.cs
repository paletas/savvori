namespace Savvori.WebApi.Modeling;

/// <summary>Configuration for the remote model backend ("Model" section). Everything defaults to off/safe.</summary>
public sealed class ModelOptions
{
    public const string SectionName = "Model";

    /// <summary>Master feature flag. When false no model call is ever made and the drain job does nothing.</summary>
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = string.Empty;
    public string EmbeddingModel { get; set; } = string.Empty;
    public string JudgeModel { get; set; } = string.Empty;
    public int ConnectTimeoutSeconds { get; set; } = 5;
    public int RequestTimeoutSeconds { get; set; } = 60;
    /// <summary>Texts per embedding request, and jobs claimed per drain run.</summary>
    public int BatchSize { get; set; } = 32;
    public int MaxConcurrency { get; set; } = 2;
    public BreakerOptions Breaker { get; set; } = new();
    public QueueOptions Queue { get; set; } = new();

    public sealed class BreakerOptions
    {
        public int FailureThreshold { get; set; } = 5;
        public int CooldownSeconds { get; set; } = 300;
    }

    public sealed class QueueOptions
    {
        public int PollSeconds { get; set; } = 60;
        public int MaxAttempts { get; set; } = 8;
        public int BaseDelaySeconds { get; set; } = 30;
        public int MaxDelaySeconds { get; set; } = 3600;
        public int LeaseSeconds { get; set; } = 600;
    }
}
