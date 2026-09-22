using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Savvori.WebApi.Modeling;

/// <summary>
/// Centralised OpenTelemetry instrumentation for the model-assisted matching/categorisation pipeline
/// (Modeling/*). Register as a singleton so ActivitySource, Meter and the sampled gauges are shared
/// across scopes. Mirrors Scraping/ScrapingTelemetry.cs.
/// </summary>
public sealed class ModelTelemetry : IDisposable
{
    public const string ActivitySourceName = "Savvori.Model";
    public const string MeterName = "Savvori.Model";

    private readonly ActivitySource _source = new(ActivitySourceName, "1.0.0");
    private readonly Meter _meter = new(MeterName, "1.0.0");

    // ---- Counters ---------------------------------------------------------------------------------
    // job.name is one of: embedding-scan, embed, candidate-generation, matching, judge, category-classifier,
    // queue-drain (the Quartz jobs plus the per-item job handlers they drive).

    /// <summary>Model pipeline runs started.</summary>
    public readonly Counter<long> RunsStarted;

    /// <summary>Model pipeline runs that completed without throwing (a skipped run - flag off, degraded mode - still counts as completed).</summary>
    public readonly Counter<long> RunsCompleted;

    /// <summary>Model pipeline runs that threw an unhandled exception.</summary>
    public readonly Counter<long> RunsFailed;

    /// <summary>Product embeddings written.</summary>
    public readonly Counter<long> EmbeddingsCreated;

    /// <summary>Match candidates added/updated/removed by candidate generation. Tag: change.</summary>
    public readonly Counter<long> CandidatesChanged;

    /// <summary>Match candidates sorted by a matching run. Tag: outcome (confident/judge-queued/review/left).</summary>
    public readonly Counter<long> MatchesDecided;

    /// <summary>Products the category classifier assigned or suggested for. Tag: outcome (assigned/confident/review/none).</summary>
    public readonly Counter<long> CategoriesDecided;

    // ---- Histograms ---------------------------------------------------------------------------------

    /// <summary>Wall-clock duration of one model pipeline run. Tag: job.name.</summary>
    public readonly Histogram<double> RunDurationMs;

    // ---- Gauges (sampled every ~30s by ModelTelemetrySampler; see there) ----------------------------

    private volatile int _queuePending, _queueRunning, _queueDeadLetter;
    private volatile int _breakerOpen; // 0 = closed, 1 = open or half-open

    public ModelTelemetry()
    {
        RunsStarted = _meter.CreateCounter<long>(
            "savvori.model.runs.started", "runs",
            "Model pipeline runs started (embedding scan, embed, candidate generation, matching, judge, category classifier, queue drain).");

        RunsCompleted = _meter.CreateCounter<long>(
            "savvori.model.runs.completed", "runs",
            "Model pipeline runs that completed without throwing.");

        RunsFailed = _meter.CreateCounter<long>(
            "savvori.model.runs.failed", "runs",
            "Model pipeline runs that threw an unhandled exception.");

        EmbeddingsCreated = _meter.CreateCounter<long>(
            "savvori.model.embeddings.created", "embeddings",
            "Product embeddings written to the database.");

        CandidatesChanged = _meter.CreateCounter<long>(
            "savvori.model.candidates.changed", "candidates",
            "Match candidates added, updated or removed by a candidate generation run.");

        MatchesDecided = _meter.CreateCounter<long>(
            "savvori.model.matches.decided", "candidates",
            "Match candidates sorted into an outcome by a matching run.");

        CategoriesDecided = _meter.CreateCounter<long>(
            "savvori.model.categories.decided", "products",
            "Products the category classifier assigned or suggested a category for.");

        RunDurationMs = _meter.CreateHistogram<double>(
            "savvori.model.run.duration", "ms",
            "Wall-clock duration of a complete model pipeline run.");

        _meter.CreateObservableGauge<int>("savvori.model.queue.depth", () =>
        [
            new Measurement<int>(_queuePending, new KeyValuePair<string, object?>("status", "pending")),
            new Measurement<int>(_queueRunning, new KeyValuePair<string, object?>("status", "running")),
            new Measurement<int>(_queueDeadLetter, new KeyValuePair<string, object?>("status", "dead-letter")),
        ], "jobs", "Model job queue depth by status (ModelJobs table), sampled every ~30s.");

        _meter.CreateObservableGauge("savvori.model.breaker.open",
            () => new Measurement<int>(_breakerOpen), "1 = circuit breaker open/half-open (model calls suspended), 0 = closed.");
    }

    /// <summary>Called by ModelTelemetrySampler after each sample.</summary>
    public void SetQueueDepth(int pending, int running, int deadLetter)
    {
        _queuePending = pending;
        _queueRunning = running;
        _queueDeadLetter = deadLetter;
    }

    /// <summary>Called by ModelTelemetrySampler after each sample.</summary>
    public void SetBreakerOpen(bool open) => _breakerOpen = open ? 1 : 0;

    /// <summary>Starts the root span for one model pipeline run.</summary>
    public Activity? StartRunActivity(string jobName) =>
        _source.StartActivity($"model {jobName}", ActivityKind.Internal)
               ?.SetTag("job.system", "quartz")
               ?.SetTag("model.job.name", jobName);

    public void Dispose()
    {
        _source.Dispose();
        _meter.Dispose();
    }
}
