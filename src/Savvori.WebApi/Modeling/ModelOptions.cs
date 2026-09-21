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
    public MatchingOptions Matching { get; set; } = new();
    public CategoryOptions Categories { get; set; } = new();

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

    public sealed class CategoryOptions
    {
        /// <summary>While true nothing is assigned: every prediction goes to the review queue. Defaults to TRUE.</summary>
        public bool DryRun { get; set; } = true;
        public string Cron { get; set; } = "0 0 4 * * ?";
        /// <summary>Nearest labelled neighbours per listing.</summary>
        public int K { get; set; } = 7;
        /// <summary>Auto-assign at or above this similarity-weighted vote share.</summary>
        public double AutoAssignConfidence { get; set; } = 0.85;
        /// <summary>From here up to the auto-assign level goes to the review queue; lower stays uncategorised.</summary>
        public double ReviewMinConfidence { get; set; } = 0.5;
        /// <summary>Neighbours less similar than this do not vote.</summary>
        public double MinNeighbourCosine { get; set; } = 0.5;
        /// <summary>A raw store-category string is decided as a whole only with at least this many uncategorised products.</summary>
        public int MinStringSupport { get; set; } = 5;
    }

    public sealed class MatchingOptions
    {
        /// <summary>
        /// While true nothing is linked: proposals go to the review queue instead. Defaults to TRUE so the first
        /// run is a dry run; set to false to let the tiers apply matches.
        /// </summary>
        public bool DryRun { get; set; } = true;
        public string Cron { get; set; } = "0 45 3 * * ?";
        /// <summary>Tier B: auto-accept at or above this cosine when both sizes are known.</summary>
        public double AcceptCosineSizeKnown { get; set; } = 0.90;
        /// <summary>Tier B: auto-accept at or above this cosine when a size is unknown.</summary>
        public double AcceptCosineSizeUnknown { get; set; } = 0.95;
        /// <summary>Tier C: ask the judge from this cosine up (size known) to the accept threshold.</summary>
        public double JudgeLowerSizeKnown { get; set; } = 0.80;
        public double JudgeLowerSizeUnknown { get; set; } = 0.85;
        /// <summary>Below the judge band, pairs from this cosine up go to the review queue; lower ones just stay proposals.</summary>
        public double ReviewMinCosine { get; set; } = 0.70;
        /// <summary>Tier B needs the brand check to have positively passed (Ok), not merely "unknown".</summary>
        public bool AutoAcceptRequiresBrandOk { get; set; } = true;
        /// <summary>
        /// When false (default) a judge "yes" never applies a match by itself, not even with dry run off: it stays in the
        /// review queue with the judge's answer. Only confident cosine pairs are applied automatically. On real data the
        /// judge said yes to some own-brand vs branded pairs.
        /// </summary>
        public bool AutoApplyJudgeYes { get; set; }
        /// <summary>Cap on judge requests queued per matching run, so the first run cannot flood the model.</summary>
        public int MaxJudgeJobsPerRun { get; set; } = 1000;
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
