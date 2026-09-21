using Microsoft.Extensions.Options;
using Quartz;
using Savvori.Shared;

namespace Savvori.WebApi.Modeling;

/// <summary>
/// Drains the model job queue in small batches. Does nothing when the feature flag is off, and nothing
/// but a health probe while the circuit breaker is open. Never touches scraping or request paths.
/// One run keeps claiming batches until the queue is empty, the breaker opens or MaxBatchesPerRun is reached.
/// </summary>
[DisallowConcurrentExecution]
public sealed class ModelQueueDrainJob(
    IOptions<ModelOptions> options,
    ModelCircuitBreaker breaker,
    IEmbeddingClient embeddings,
    IServiceScopeFactory scopes,
    ILogger<ModelQueueDrainJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var opts = options.Value;
        if (!opts.Enabled) return;
        var ct = context.CancellationToken;

        // Degraded mode: only probe. A successful probe closes the breaker; work resumes on the next run.
        if (!breaker.IsClosed)
        {
            try
            {
                await embeddings.PingAsync(ct);
                logger.LogInformation("Model server responded to probe; circuit breaker closed.");
            }
            catch (ModelUnavailableException)
            {
                // Still down or cooling down; the breaker recorded it.
            }
            return;
        }

        for (var round = 0; round < Math.Max(1, opts.Queue.MaxBatchesPerRun) && breaker.IsClosed; round++)
        {
            ct.ThrowIfCancellationRequested();
            List<Guid> claimed;
            using (var scope = scopes.CreateScope())
            {
                var types = scope.ServiceProvider.GetServices<IModelJobHandler>().Select(h => h.Type).Distinct().ToList();
                if (types.Count == 0) return;
                claimed = await scope.ServiceProvider.GetRequiredService<ModelJobQueue>()
                    .ClaimDueAsync(types, Math.Max(1, opts.BatchSize) * Math.Max(1, opts.MaxConcurrency), ct);
            }
            if (claimed.Count == 0) return;

            // Chunks of BatchSize: one model request per chunk for batch handlers, one job at a time otherwise.
            var chunks = claimed.Chunk(Math.Max(1, opts.BatchSize)).ToList();
            await Parallel.ForEachAsync(chunks,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, opts.MaxConcurrency), CancellationToken = ct },
                async (chunk, token) => await RunChunkAsync(chunk, token));
        }
    }

    private async Task RunChunkAsync(Guid[] ids, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<ModelJobQueue>();
        var handlers = scope.ServiceProvider.GetServices<IModelJobHandler>().ToList();

        var jobs = new List<ModelJob>();
        foreach (var id in ids)
            if (await queue.GetAsync(id, ct) is { } job) jobs.Add(job);

        foreach (var group in jobs.GroupBy(j => j.Type))
        {
            var handler = handlers.FirstOrDefault(h => h.Type == group.Key);
            if (handler is null || !breaker.IsClosed)
            {
                foreach (var job in group)
                    await queue.ReleaseAsync(job.Id, breaker.Snapshot().RetryAt, CancellationToken.None);
                continue;
            }

            var list = group.ToList();
            if (handler is IBatchModelJobHandler batch)
                await RunAsync(queue, list, () => batch.HandleBatchAsync(list, ct), ct);
            else
                foreach (var job in list)
                    await RunAsync(queue, [job], () => handler.HandleAsync(job, ct), ct);
        }
    }

    private async Task RunAsync(ModelJobQueue queue, List<ModelJob> jobs, Func<Task> work, CancellationToken ct)
    {
        try
        {
            await work();
            foreach (var job in jobs) await queue.CompleteAsync(job.Id, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down: not the job's fault.
            foreach (var job in jobs) await queue.ReleaseAsync(job.Id, null, CancellationToken.None);
        }
        catch (ModelUnavailableException ex)
        {
            // If the breaker is now open the whole model is down: defer without using up an attempt.
            // If it is still closed this may be a job-specific failure, so it counts.
            var systemWide = !breaker.IsClosed;
            logger.LogWarning("{Count} model job(s) failed (model unavailable): {Error}", jobs.Count, ex.Message);
            foreach (var job in jobs)
                await queue.FailAsync(job.Id, ex.Message, countAttempt: !systemWide,
                    systemWide ? breaker.Snapshot().RetryAt : null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Count} model job(s) failed.", jobs.Count);
            foreach (var job in jobs)
                await queue.FailAsync(job.Id, ex.Message, countAttempt: true, ct: CancellationToken.None);
        }
    }
}
