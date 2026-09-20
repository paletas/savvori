using Microsoft.Extensions.Options;
using Quartz;

namespace Savvori.WebApi.Modeling;

/// <summary>
/// Drains the model job queue in small batches. Does nothing when the feature flag is off, and nothing
/// but a health probe while the circuit breaker is open. Never touches scraping or request paths.
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

        List<Guid> claimed;
        using (var scope = scopes.CreateScope())
        {
            var types = scope.ServiceProvider.GetServices<IModelJobHandler>().Select(h => h.Type).Distinct().ToList();
            if (types.Count == 0) return;
            claimed = await scope.ServiceProvider.GetRequiredService<ModelJobQueue>()
                .ClaimDueAsync(types, opts.BatchSize, ct);
        }
        if (claimed.Count == 0) return;

        await Parallel.ForEachAsync(claimed,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, opts.MaxConcurrency), CancellationToken = ct },
            async (id, token) => await RunOneAsync(id, token));
    }

    private async Task RunOneAsync(Guid id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<ModelJobQueue>();
        var job = await queue.GetAsync(id, ct);
        if (job is null) return;

        var retryAt = () => breaker.Snapshot().RetryAt;
        if (!breaker.IsClosed)
        {
            await queue.ReleaseAsync(id, retryAt(), CancellationToken.None);
            return;
        }

        var handler = scope.ServiceProvider.GetServices<IModelJobHandler>().FirstOrDefault(h => h.Type == job.Type);
        if (handler is null)
        {
            await queue.ReleaseAsync(id, null, CancellationToken.None);
            return;
        }

        try
        {
            await handler.HandleAsync(job, ct);
            await queue.CompleteAsync(id, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await queue.ReleaseAsync(id, null, CancellationToken.None); // shutting down: not the job's fault
        }
        catch (ModelUnavailableException ex)
        {
            // If the breaker is now open the whole model is down: defer without using up an attempt.
            // If it is still closed this may be a job-specific failure, so it counts.
            var systemWide = !breaker.IsClosed;
            logger.LogWarning("Model job {JobId} failed (model unavailable): {Error}", id, ex.Message);
            await queue.FailAsync(id, ex.Message, countAttempt: !systemWide, systemWide ? retryAt() : null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Model job {JobId} failed.", id);
            await queue.FailAsync(id, ex.Message, countAttempt: true, ct: CancellationToken.None);
        }
    }
}
