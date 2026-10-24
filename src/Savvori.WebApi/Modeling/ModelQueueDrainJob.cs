using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Quartz;
using Savvori.Shared;

namespace Savvori.WebApi.Modeling;

/// <summary>
/// Drains the model job queue in small batches. Does nothing when the feature flag is off. Each backend has its
/// own circuit breaker and probe: while a backend's breaker is open it is only probed and its job types are not
/// claimed, while the other backend keeps draining. A backend that answers 429 stops for the rest of the run
/// (its jobs are deferred, never failed). Never touches scraping or request paths.
/// One run keeps claiming batches until the queue is empty, no backend is runnable or MaxBatchesPerRun is reached.
/// </summary>
[DisallowConcurrentExecution]
public sealed class ModelQueueDrainJob(
    IOptions<ModelOptions> options,
    ModelBreakers breakers,
    IEnumerable<IModelProbe> probes,
    ThrottleTracker throttles,
    TimeProvider time,
    IServiceScopeFactory scopes,
    ModelTelemetry telemetry,
    ILogger<ModelQueueDrainJob> logger) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        var opts = options.Value;
        if (!opts.Enabled) return;
        var ct = context.CancellationToken;
        var run = new ConcurrentDictionary<ModelBackend, DateTime>();

        // Degraded mode per backend: only probe. A successful probe closes the breaker; that backend's work
        // resumes on the next run.
        var probed = new HashSet<ModelBackend>();
        foreach (var (backend, breaker) in breakers.All)
        {
            if (breaker.IsClosed) continue;
            probed.Add(backend);
            var probe = probes.FirstOrDefault(p => p.Backend == backend);
            if (probe is null) continue;
            try
            {
                await probe.PingAsync(ct);
                logger.LogInformation("Backend {Backend} responded to probe; circuit breaker closed.", backend);
            }
            catch (Exception ex) when (ex is ModelUnavailableException or ModelResponseException or ModelThrottledException)
            {
                // Still down, cooling down or throttled; the breaker recorded what it needed to.
            }
        }

        for (var round = 0; round < Math.Max(1, opts.Queue.MaxBatchesPerRun); round++)
        {
            ct.ThrowIfCancellationRequested();
            var runnable = breakers.All
                .Where(b => b.Breaker.IsClosed && !probed.Contains(b.Backend) && !run.ContainsKey(b.Backend))
                .Select(b => b.Backend).ToHashSet();
            if (runnable.Count == 0) return;

            List<Guid> claimed;
            using (var scope = scopes.CreateScope())
            {
                var types = scope.ServiceProvider.GetServices<IModelJobHandler>().Select(h => h.Type).Distinct()
                    .Where(t => runnable.Contains(ModelBackends.For(t))).ToList();
                if (types.Count == 0) return;
                claimed = await scope.ServiceProvider.GetRequiredService<ModelJobQueue>()
                    .ClaimDueAsync(types, Math.Max(1, opts.BatchSize) * Math.Max(1, opts.MaxConcurrency), ct);
            }
            if (claimed.Count == 0) return;

            // Chunks of BatchSize: one model request per chunk for batch handlers, one job at a time otherwise.
            // A run with nothing claimed (the common case, polled every 60s) emits no metrics on purpose - it
            // would otherwise dominate savvori.model.runs.* with noise next to the nightly jobs.
            var chunks = claimed.Chunk(Math.Max(1, opts.BatchSize)).ToList();
            await Parallel.ForEachAsync(chunks,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, opts.MaxConcurrency), CancellationToken = ct },
                async (chunk, token) => await RunChunkAsync(chunk, run, token));
        }
    }

    private async Task RunChunkAsync(Guid[] ids, ConcurrentDictionary<ModelBackend, DateTime> run, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<ModelJobQueue>();
        var handlers = scope.ServiceProvider.GetServices<IModelJobHandler>().ToList();

        var jobs = new List<ModelJob>();
        foreach (var id in ids)
            if (await queue.GetAsync(id, ct) is { } job) jobs.Add(job);

        foreach (var group in jobs.GroupBy(j => j.Type))
        {
            var backend = ModelBackends.For(group.Key);
            var handler = handlers.FirstOrDefault(h => h.Type == group.Key);
            if (handler is null || !breakers.For(backend).IsClosed || run.ContainsKey(backend))
            {
                foreach (var job in group)
                    await queue.ReleaseAsync(job.Id, run.TryGetValue(backend, out var u) ? u : breakers.For(backend).Snapshot().RetryAt, CancellationToken.None);
                continue;
            }

            var list = group.ToList();
            var jobName = group.Key == ModelJobType.Embed ? "embed" : group.Key.ToString().ToLowerInvariant();
            using var activity = telemetry.StartRunActivity(jobName);
            telemetry.RunsStarted.Add(1, new KeyValuePair<string, object?>("job.name", jobName));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ok = handler is IBatchModelJobHandler batch
                ? await RunAsync(scope.ServiceProvider, queue, list, () => batch.HandleBatchAsync(list, ct), run, ct)
                : await RunManyAsync(scope.ServiceProvider, queue, list, job => handler.HandleAsync(job, ct), run, ct);
            telemetry.RunDurationMs.Record(sw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("job.name", jobName));
            (ok ? telemetry.RunsCompleted : telemetry.RunsFailed).Add(1, new KeyValuePair<string, object?>("job.name", jobName));
        }
    }

    /// <summary>Runs one job at a time (non-batch handlers) so one bad job doesn't fail its siblings; the group's
    /// telemetry outcome is "ok" only if every job in it succeeded. Once the backend is throttled, the rest of the
    /// group is put back with the same deferral instead of being run.</summary>
    private async Task<bool> RunManyAsync(IServiceProvider sp, ModelJobQueue queue, List<ModelJob> jobs, Func<ModelJob, Task> work,
        ConcurrentDictionary<ModelBackend, DateTime> run, CancellationToken ct)
    {
        var allOk = true;
        foreach (var job in jobs)
        {
            if (run.TryGetValue(ModelBackends.For(job.Type), out var until))
            {
                await queue.ReleaseAsync(job.Id, until, CancellationToken.None);
                continue;
            }
            var ok = await RunAsync(sp, queue, [job], () => work(job), run, ct);
            allOk = allOk && ok;
        }
        return allOk;
    }

    /// <summary>
    /// Runs one unit of work (a batch, or a single job) and settles every job in it. Never throws for a job-level
    /// failure - errors are recorded on the queue row and swallowed, exactly as before this method returned a bool
    /// instead of void, so one failing chunk does not stop the other chunks in this run's Parallel.ForEachAsync.
    /// A 429 defers the work (no attempt spent, breaker untouched) and stops that backend for the rest of the run.
    /// </summary>
    private async Task<bool> RunAsync(IServiceProvider sp, ModelJobQueue queue, List<ModelJob> jobs, Func<Task> work,
        ConcurrentDictionary<ModelBackend, DateTime> run, CancellationToken ct)
    {
        var backend = ModelBackends.For(jobs[0].Type);
        var breaker = breakers.For(backend);
        try
        {
            await work();
            foreach (var job in jobs)
            {
                throttles.Clear(job.Id);
                await queue.CompleteAsync(job.Id, ct);
            }
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down: not the job's fault, and not a telemetry-worthy failure either.
            foreach (var job in jobs) await queue.ReleaseAsync(job.Id, null, CancellationToken.None);
            return true;
        }
        catch (ModelThrottledException ex)
        {
            // The backend is alive but throttling us: not an error. Only the job whose call returned the 429 is
            // counted towards the back-off; its siblings are put back untouched with the same wait.
            var first = jobs[0];
            var delay = ModelDeferral.Compute(ex.Kind, ex.RetryAfter, throttles.Prior(first.Id), options.Value.Queue, Random.Shared.NextDouble());
            var until = time.GetUtcNow().UtcDateTime + delay;
            if (ex.Kind != ThrottleKind.Quota) throttles.Record(first.Id);
            run.AddOrUpdate(backend, until, (_, old) => old > until ? old : until);
            await queue.DeferAsync(first.Id, until, ex.Message, CancellationToken.None);
            foreach (var job in jobs.Skip(1))
                await queue.ReleaseAsync(job.Id, until, CancellationToken.None);
            logger.LogWarning("{Backend} throttled ({Kind}); deferring {Count} job(s) until {Until}; backend stopped for this run",
                backend, ex.Kind, jobs.Count, until);
            return true;
        }
        catch (ModelUnavailableException ex)
        {
            // If the breaker is now open the whole backend is down: defer without using up an attempt.
            // If it is still closed this may be a job-specific failure, so it counts.
            var systemWide = !breaker.IsClosed;
            logger.LogWarning("{Count} model job(s) failed (model unavailable): {Error}", jobs.Count, ex.Message);
            foreach (var job in jobs)
            {
                throttles.Clear(job.Id);
                await queue.FailAsync(job.Id, ex.Message, countAttempt: !systemWide,
                    systemWide ? breaker.Snapshot().RetryAt : null, CancellationToken.None);
            }
            await ReviewDeadLetteredJudgeJobsAsync(sp, queue, jobs);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Count} model job(s) failed.", jobs.Count);
            foreach (var job in jobs)
            {
                throttles.Clear(job.Id);
                await queue.FailAsync(job.Id, ex.Message, countAttempt: true, ct: CancellationToken.None);
            }
            await ReviewDeadLetteredJudgeJobsAsync(sp, queue, jobs);
            return false;
        }
    }

    /// <summary>
    /// A judge job that used up its attempts will never produce a verdict: send its candidate to the review list
    /// with no verdict or score, so a human decides instead of the pair sitting in PendingJudge forever.
    /// Never throws: a failure here must not break the run.
    /// </summary>
    private async Task ReviewDeadLetteredJudgeJobsAsync(IServiceProvider sp, ModelJobQueue queue, IEnumerable<ModelJob> jobs)
    {
        try
        {
            var db = sp.GetRequiredService<SavvoriDbContext>();
            foreach (var job in jobs.Where(j => j.Type == ModelJobType.Judge))
            {
                var fresh = await queue.GetAsync(job.Id, CancellationToken.None);
                if (fresh?.Status != ModelJobStatus.DeadLetter) continue;
                var c = await db.MatchCandidates.FirstOrDefaultAsync(x => x.Id == job.SubjectId);
                if (c is not { Status: CandidateStatus.PendingJudge }) continue;
                c.Status = CandidateStatus.NeedsReview;
                c.JudgeVerdict = null;
                c.JudgeScore = null;
                c.Note = "Judge failed " + fresh.Attempts + " times; needs a human.";
                await db.SaveChangesAsync(CancellationToken.None);
                telemetry.MatchesDecided.Add(1, new KeyValuePair<string, object?>("outcome", "judge-deadletter"));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not send dead-lettered judge job(s) to review.");
        }
    }
}
