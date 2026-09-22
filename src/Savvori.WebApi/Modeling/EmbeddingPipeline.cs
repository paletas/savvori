using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Quartz;
using Savvori.Shared;

namespace Savvori.WebApi.Modeling;

/// <summary>The last model identity we saw served; lets staleness checks work without calling the model.</summary>
public sealed class CurrentModelState
{
    private volatile ModelInfo? _info;
    public ModelInfo? Info { get => _info; set => _info = value; }
}

public sealed record ScanResult(int ActiveProducts, int Missing, int Stale, int Enqueued);

/// <summary>Works out which active products need (re-)embedding and queues idempotent jobs for them. No model calls.</summary>
public sealed class EmbeddingScanner(
    SavvoriDbContext db, ModelJobQueue queue, IOptions<ModelOptions> options)
{
    public sealed record Needs(int ActiveProducts, List<(Guid Id, string Hash)> Missing, List<(Guid Id, string Hash)> Stale);

    /// <summary>
    /// Missing: no embedding row. Stale: model name/digest or the exact input text changed. When the current digest
    /// is unknown (model never reached yet) the digest is not judged, only the name and the text.
    /// </summary>
    public async Task<Needs> FindNeedsAsync(ModelInfo? current, CancellationToken ct = default)
    {
        var products = await db.StoreProducts.AsNoTracking().Where(sp => sp.IsActive)
            .Select(sp => new { sp.Id, sp.Name, sp.Brand }).ToListAsync(ct);
        var embeddings = await db.StoreProductEmbeddings.AsNoTracking()
            .Select(e => new { e.StoreProductId, e.ModelName, e.ModelDigest, e.Dimension, e.InputTextHash })
            .ToDictionaryAsync(e => e.StoreProductId, ct);

        var modelName = options.Value.EmbeddingModel;
        var missing = new List<(Guid, string)>();
        var stale = new List<(Guid, string)>();
        foreach (var p in products)
        {
            var hash = EmbeddingFreshness.HashText(EmbeddingFreshness.BuildInputText(p.Brand, p.Name));
            if (!embeddings.TryGetValue(p.Id, out var e)) { missing.Add((p.Id, hash)); continue; }

            var stored = new EmbeddingMetadata(e.ModelName, e.ModelDigest, e.Dimension, e.InputTextHash);
            var now = new EmbeddingMetadata(modelName, current?.ModelDigest ?? e.ModelDigest, e.Dimension, hash);
            if (EmbeddingFreshness.IsStale(stored, now)) stale.Add((p.Id, hash));
        }
        return new Needs(products.Count, missing, stale);
    }

    public async Task<ScanResult> ScanAsync(ModelInfo? current, CancellationToken ct = default)
    {
        var needs = await FindNeedsAsync(current, ct);
        var enqueued = await queue.EnqueueManyAsync(ModelJobType.Embed, [.. needs.Missing, .. needs.Stale], ct);
        return new ScanResult(needs.ActiveProducts, needs.Missing.Count, needs.Stale.Count, enqueued);
    }
}

/// <summary>Real stale-embedding count for the status panel, based on the last model identity we saw.</summary>
public sealed class DbStaleEmbeddingSource(EmbeddingScanner scanner, CurrentModelState state) : IStaleEmbeddingSource
{
    public async Task<int> CountStaleAsync(CancellationToken ct = default) =>
        (await scanner.FindNeedsAsync(state.Info, ct)).Stale.Count;
}

/// <summary>Embeds queued products in batches and stores each vector with its full provenance.</summary>
public sealed class EmbedJobHandler(
    SavvoriDbContext db, IEmbeddingClient client, CurrentModelState state, TimeProvider time, ModelTelemetry telemetry) : IBatchModelJobHandler
{
    public ModelJobType Type => ModelJobType.Embed;

    public Task HandleAsync(ModelJob job, CancellationToken ct) => HandleBatchAsync([job], ct);

    public async Task HandleBatchAsync(IReadOnlyList<ModelJob> jobs, CancellationToken ct)
    {
        var ids = jobs.Select(j => j.SubjectId).Distinct().ToList();
        var products = await db.StoreProducts.AsNoTracking().Where(sp => ids.Contains(sp.Id))
            .Select(sp => new { sp.Id, sp.Name, sp.Brand }).ToDictionaryAsync(sp => sp.Id, ct);

        // A job whose product vanished, or whose text has changed since it was queued, is obsolete:
        // the scan queues a fresh job for the new text. Completing it without work is correct and idempotent.
        var work = new List<(Guid Id, string Text, string Hash)>();
        foreach (var job in jobs)
        {
            if (!products.TryGetValue(job.SubjectId, out var p)) continue;
            var text = EmbeddingFreshness.BuildInputText(p.Brand, p.Name);
            var hash = EmbeddingFreshness.HashText(text);
            if (hash == job.PayloadHash && work.All(w => w.Id != p.Id)) work.Add((p.Id, text, hash));
        }
        if (work.Count == 0) return;

        var result = await client.EmbedAsync(work.Select(w => w.Text).ToList(), ct);
        if (result.Vectors.Count != work.Count)
            throw new ModelResponseException($"Expected {work.Count} vectors, got {result.Vectors.Count}.");
        state.Info = new ModelInfo(result.ModelName, result.ModelDigest);

        var workIds = work.Select(w => w.Id).ToList();
        var existing = await db.StoreProductEmbeddings
            .Where(e => workIds.Contains(e.StoreProductId)).ToDictionaryAsync(e => e.StoreProductId, ct);
        var now = time.GetUtcNow().UtcDateTime;
        for (var i = 0; i < work.Count; i++)
        {
            if (!existing.TryGetValue(work[i].Id, out var row))
            {
                row = new StoreProductEmbedding { StoreProductId = work[i].Id };
                db.StoreProductEmbeddings.Add(row);
            }
            row.Vector = VectorCodec.ToBytes(result.Vectors[i]);
            row.ModelName = result.ModelName;
            row.ModelDigest = result.ModelDigest;
            row.Dimension = result.Dimension;
            row.InputTextHash = work[i].Hash;
            row.EmbeddedAt = now;
        }
        await db.SaveChangesAsync(ct);
        telemetry.EmbeddingsCreated.Add(work.Count);
    }
}

/// <summary>Hourly: queue embedding jobs for new/changed products. Skipped entirely while the feature flag is off.</summary>
[DisallowConcurrentExecution]
public sealed class EmbeddingScanJob(
    IOptions<ModelOptions> options, IServiceScopeFactory scopes, CurrentModelState state,
    ModelTelemetry telemetry, ILogger<EmbeddingScanJob> logger) : IJob
{
    private const string JobName = "embedding-scan";

    public async Task Execute(IJobExecutionContext context)
    {
        if (!options.Value.Enabled) return;
        var ct = context.CancellationToken;
        using var activity = telemetry.StartRunActivity(JobName);
        telemetry.RunsStarted.Add(1, new KeyValuePair<string, object?>("job.name", JobName));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var scope = scopes.CreateScope();

            // Learn the current model identity if the model is reachable; if not, queue work anyway (name + text only).
            ModelInfo? info = null;
            try
            {
                info = await scope.ServiceProvider.GetRequiredService<IEmbeddingClient>().GetModelInfoAsync(ct);
                state.Info = info;
            }
            catch (ModelUnavailableException ex)
            {
                logger.LogInformation("Embedding scan running without model identity: {Error}", ex.Message);
            }
            catch (ModelResponseException ex)
            {
                logger.LogWarning("Embedding scan running without model identity: {Error}", ex.Message);
            }

            var result = await scope.ServiceProvider.GetRequiredService<EmbeddingScanner>().ScanAsync(info, ct);
            logger.LogInformation(
                "Embedding scan: {Active} active products, {Missing} missing, {Stale} stale, {Enqueued} jobs queued.",
                result.ActiveProducts, result.Missing, result.Stale, result.Enqueued);
            telemetry.RunsCompleted.Add(1, new KeyValuePair<string, object?>("job.name", JobName));
        }
        catch
        {
            telemetry.RunsFailed.Add(1, new KeyValuePair<string, object?>("job.name", JobName));
            throw;
        }
        finally
        {
            telemetry.RunDurationMs.Record(sw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("job.name", JobName));
        }
    }
}
