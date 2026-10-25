using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Savvori.Shared;

namespace Savvori.WebApi.Modeling;

/// <summary>What one model HTTP request is for. Task: embed | category | translate | pair | probe.</summary>
public sealed record ModelCallContext(ModelBackend Backend, string Task, string Model, int Items);

/// <summary>Records one ModelCalls row and the call metrics per model HTTP attempt. Never throws into the call.</summary>
public interface IModelCallRecorder
{
    Task RecordAsync(ModelCallContext call, ModelCallStatus status, int? httpStatus, TimeSpan latency, JudgeUsage? usage);

    /// <summary>Deletes rows older than 90 days, at most once per 24 hours.</summary>
    Task PruneIfDueAsync(CancellationToken ct);
}

public sealed class ModelCallRecorder(
    IServiceScopeFactory scopes, ModelTelemetry telemetry, TimeProvider time, ILogger<ModelCallRecorder> logger)
    : IModelCallRecorder
{
    private static readonly TimeSpan PruneEvery = TimeSpan.FromHours(24);
    private const int RetentionDays = 90;

    private readonly object _gate = new();
    private DateTime? _lastPrune;

    public async Task RecordAsync(
        ModelCallContext call, ModelCallStatus status, int? httpStatus, TimeSpan latency, JudgeUsage? usage)
    {
        // Metrics first, so they never depend on the database.
        telemetry.RecordCall(call, status, latency, usage);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SavvoriDbContext>();
            db.ModelCalls.Add(new ModelCall
            {
                Id = Guid.NewGuid(),
                At = time.GetUtcNow().UtcDateTime,
                Backend = ModelBackends.Tag(call.Backend),
                Task = call.Task,
                Model = usage?.ResolvedModel ?? call.Model,
                Status = status,
                HttpStatus = httpStatus,
                LatencyMs = (int)latency.TotalMilliseconds,
                Items = call.Items,
                PromptTokens = usage?.InputTokens,
                CompletionTokens = usage?.OutputTokens,
                CostUsd = usage?.CostUsd ?? (call.Backend == ModelBackend.Gateway && status == ModelCallStatus.Ok ? 0m : null)
            });
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not record model call");
        }
    }

    public async Task PruneIfDueAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        lock (_gate)
        {
            if (_lastPrune is { } last && now - last < PruneEvery) return;
            _lastPrune = now;
        }
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SavvoriDbContext>();
            var cutoff = now.AddDays(-RetentionDays);
            var deleted = await db.ModelCalls.Where(c => c.At < cutoff).ExecuteDeleteAsync(ct);
            logger.LogInformation("Pruned {Count} model call rows older than {Days} days", deleted, RetentionDays);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not prune model calls");
        }
    }
}

/// <summary>Recorder that does nothing, for callers (tests, hand-built clients) that pass none.</summary>
public sealed class NullModelCallRecorder : IModelCallRecorder
{
    public static readonly NullModelCallRecorder Instance = new();

    public Task RecordAsync(ModelCallContext call, ModelCallStatus status, int? httpStatus, TimeSpan latency, JudgeUsage? usage) =>
        Task.CompletedTask;

    public Task PruneIfDueAsync(CancellationToken ct) => Task.CompletedTask;
}
