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
    public ScanOptions Scan { get; set; } = new();
    public CandidateOptions Candidates { get; set; } = new();

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
        /// <summary>Upper bound on claim-process cycles per drain run, so one run cannot go on forever.</summary>
        public int MaxBatchesPerRun { get; set; } = 50;
    }

    public sealed class ScanOptions
    {
        /// <summary>Quartz cron for finding products that need (re-)embedding.</summary>
        public string Cron { get; set; } = "0 15 * * * ?";
    }

    public sealed class CandidateOptions
    {
        /// <summary>Quartz cron for candidate-pair generation (needs no model call, only stored embeddings).</summary>
        public string Cron { get; set; } = "0 30 3 * * ?";
        public double MinCosine { get; set; } = 0.6;
        public int TopK { get; set; } = 8;
        /// <summary>Relative size difference allowed when both sizes are known.</summary>
        public double SizeTolerance { get; set; } = 0.02;
    }
}
