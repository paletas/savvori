using System.Diagnostics;
using System.Diagnostics.Metrics;
using Savvori.Shared;

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

    // Per HTTP attempt (see ModelCallRecorder). Tags: backend (gateway|openrouter), task (embed|category|translate|pair|probe),
    // model (resolved or configured id), status (ok|throttled|unavailable|error).

    /// <summary>Model HTTP attempts.</summary>
    public readonly Counter<long> Calls;

    /// <summary>Tokens reported by the model server. Extra tag: kind (prompt|completion).</summary>
    public readonly Counter<long> Tokens;

    /// <summary>Spend in USD reported by the model server (Jev's usage.cost; the gateway has none).</summary>
    public readonly Counter<double> CostUsd;

    /// <summary>Attempts answered with a 429.</summary>
    public readonly Counter<long> Throttled;

    /// <summary>Job sets deferred by a 429 in the queue drain. Tags: backend, kind (quota|routing|proxy).</summary>
    public readonly Counter<long> Deferred;

    // ---- Histograms ---------------------------------------------------------------------------------

    /// <summary>Wall-clock duration of one model pipeline run. Tag: job.name.</summary>
    public readonly Histogram<double> RunDurationMs;

    /// <summary>Wall time of one model HTTP attempt. Tags as <see cref="Calls"/>.</summary>
    public readonly Histogram<double> CallDurationMs;

    // ---- Gauges (sampled every ~30s by ModelTelemetrySampler; see there) ----------------------------

    private volatile int _queuePending, _queueRunning, _queueDeadLetter;
    private volatile int _breakerOpenGateway; // 0 = closed, 1 = open or half-open
    private volatile int _breakerOpenOpenRouter;

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

        Calls = _meter.CreateCounter<long>(
            "savvori.model.calls", "calls",
            "Model HTTP attempts (one per attempt, a proxy-429 retry counts twice).");

        CallDurationMs = _meter.CreateHistogram<double>(
            "savvori.model.call.duration", "ms",
            "Wall time of one model HTTP attempt.");

        Tokens = _meter.CreateCounter<long>(
            "savvori.model.tokens", "tokens",
            "Prompt and completion tokens reported by the model server.");

        CostUsd = _meter.CreateCounter<double>(
            "savvori.model.cost", "USD",
            "Spend reported by the model server (OpenRouter usage.cost).");

        Throttled = _meter.CreateCounter<long>(
            "savvori.model.throttled", "calls",
            "Model HTTP attempts answered with a 429.");

        Deferred = _meter.CreateCounter<long>(
            "savvori.model.deferred", "jobs",
            "Job sets deferred by a 429 in the queue drain.");

        _meter.CreateObservableGauge<int>("savvori.model.queue.depth", () =>
        [
            new Measurement<int>(_queuePending, new KeyValuePair<string, object?>("status", "pending")),
            new Measurement<int>(_queueRunning, new KeyValuePair<string, object?>("status", "running")),
            new Measurement<int>(_queueDeadLetter, new KeyValuePair<string, object?>("status", "dead-letter")),
        ], "jobs", "Model job queue depth by status (ModelJobs table), sampled every ~30s.");

        _meter.CreateObservableGauge<int>("savvori.model.breaker.open", () =>
        [
            new Measurement<int>(_breakerOpenGateway, new KeyValuePair<string, object?>("backend", "gateway")),
            new Measurement<int>(_breakerOpenOpenRouter, new KeyValuePair<string, object?>("backend", "openrouter")),
        ], "1", "1 = circuit breaker open/half-open (that backend's model calls suspended), 0 = closed. Per backend.");
    }

    /// <summary>Called by ModelTelemetrySampler after each sample.</summary>
    public void SetQueueDepth(int pending, int running, int deadLetter)
    {
        _queuePending = pending;
        _queueRunning = running;
        _queueDeadLetter = deadLetter;
    }

    /// <summary>Called by ModelTelemetrySampler after each sample.</summary>
    public void SetBreakerOpen(ModelBackend backend, bool open)
    {
        if (backend == ModelBackend.Gateway) _breakerOpenGateway = open ? 1 : 0;
        else _breakerOpenOpenRouter = open ? 1 : 0;
    }

    /// <summary>Emits the per-attempt instruments for one model HTTP attempt.</summary>
    public void RecordCall(ModelCallContext c, ModelCallStatus status, TimeSpan latency, JudgeUsage? usage)
    {
        var tags = new TagList
        {
            { "backend", ModelBackends.Tag(c.Backend) },
            { "task", c.Task },
            { "model", usage?.ResolvedModel ?? c.Model },
            { "status", status.ToString().ToLowerInvariant() },
        };
        Calls.Add(1, tags);
        CallDurationMs.Record(latency.TotalMilliseconds, tags);
        if (status == ModelCallStatus.Throttled) Throttled.Add(1, tags);
        if (usage?.InputTokens is { } p)
        {
            var t = tags;
            t.Add("kind", "prompt");
            Tokens.Add(p, t);
        }
        if (usage?.OutputTokens is { } o)
        {
            var t = tags;
            t.Add("kind", "completion");
            Tokens.Add(o, t);
        }
        if (usage?.CostUsd is { } cost && cost > 0) CostUsd.Add((double)cost, tags);
    }

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
