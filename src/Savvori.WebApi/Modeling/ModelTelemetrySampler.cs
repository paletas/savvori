using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Savvori.Shared;

namespace Savvori.WebApi.Modeling;

/// <summary>
/// Samples the model job queue depth and circuit-breaker state every ~30s and publishes them through
/// ModelTelemetry's gauges. A separate loop rather than an OpenTelemetry ObservableGauge callback because
/// the queue depth needs a DbContext, and gauge callbacks must stay synchronous and cheap.
/// </summary>
public sealed class ModelTelemetrySampler(
    IServiceScopeFactory scopes, ModelTelemetry telemetry, ModelCircuitBreaker breaker,
    IOptions<ModelOptions> options, ILogger<ModelTelemetrySampler> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            telemetry.SetBreakerOpen(!breaker.IsClosed);

            if (options.Value.Enabled)
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<SavvoriDbContext>();
                    var counts = await db.ModelJobs.AsNoTracking()
                        .GroupBy(j => j.Status)
                        .Select(g => new { Status = g.Key, Count = g.Count() })
                        .ToListAsync(stoppingToken);
                    int Get(ModelJobStatus s) => counts.FirstOrDefault(c => c.Status == s)?.Count ?? 0;
                    telemetry.SetQueueDepth(Get(ModelJobStatus.Pending), Get(ModelJobStatus.Running), Get(ModelJobStatus.DeadLetter));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "Model queue depth sample failed; leaving the previous values in place.");
                }
            }
            else
            {
                telemetry.SetQueueDepth(0, 0, 0);
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { }
        }
    }
}
