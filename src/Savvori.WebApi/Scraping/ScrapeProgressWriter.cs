using Microsoft.EntityFrameworkCore;

namespace Savvori.WebApi.Scraping;

/// <summary>
/// Persists live progress and a keep-alive heartbeat for one running job through short, separate DbContext
/// scopes, so it never touches the job's own change tracker. Every write failure is logged and swallowed:
/// observability must never fail or block a scrape.
/// </summary>
public sealed class ScrapeProgressWriter : IAsyncDisposable
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger _logger;
    private readonly Guid _jobId;
    private readonly TimeSpan _heartbeatInterval;
    private readonly CancellationTokenSource _cts = new();
    private Task? _heartbeat;
    private Task _lastWrite = Task.CompletedTask;
    private int _writing;

    public ScrapeProgressWriter(IServiceScopeFactory scopes, ILogger logger, Guid jobId, TimeSpan heartbeatInterval)
    {
        _scopes = scopes;
        _logger = logger;
        _jobId = jobId;
        _heartbeatInterval = heartbeatInterval;
    }

    /// <summary>Starts the heartbeat timer, bound to this writer's lifetime.</summary>
    public void Start()
    {
        _heartbeat = Task.Run(async () =>
        {
            await WriteAsync(j => j.LastHeartbeatAt = DateTime.UtcNow);
            using var timer = new PeriodicTimer(_heartbeatInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(_cts.Token))
                    await WriteAsync(j => j.LastHeartbeatAt = DateTime.UtcNow);
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>Fire-and-forget progress write; a snapshot arriving while one is in flight is dropped.</summary>
    public void Report(ScrapeProgress p)
    {
        if (Interlocked.CompareExchange(ref _writing, 1, 0) != 0) return;
        _lastWrite = Task.Run(async () =>
        {
            try
            {
                // A declared total only makes sense when the whole run is one key.
                var doneKeysJson = p.DoneKeys is null ? null : System.Text.Json.JsonSerializer.Serialize(p.DoneKeys);
                int? total = p.KeysTotal is 1 && p.KeySiteTotal is > 0 ? p.KeySiteTotal : null;
                await WriteAsync(j =>
                {
                    j.ProgressProductsSeen = p.ProductsSeen;
                    j.ProgressKey = p.Key is { Length: > 300 } ? p.Key[..300] : p.Key;
                    j.ProgressOffset = p.Offset;
                    j.ProgressTotal = total ?? j.ProgressTotal;
                    j.ProgressKeysDone = p.KeysDone;
                    j.ProgressKeysTotal = p.KeysTotal;
                    j.ProgressPagesDone = p.PagesDone;
                    j.ProgressKeyPages = p.KeyPages;
                    j.ProgressKeyPagesTotal = p.KeyPagesTotal;
                    j.ProgressDoneKeys = doneKeysJson;
                    j.LastProgressAt = DateTime.UtcNow;
                });
            }
            finally { Interlocked.Exchange(ref _writing, 0); }
        });
    }

    /// <summary>Marks the post-scrape processing phase so the UI doesn't look idle.</summary>
    public Task MarkProcessingAsync()
        => WriteAsync(j =>
        {
            j.ProgressKey = "processing";
            j.LastProgressAt = DateTime.UtcNow;
        });

    /// <summary>Stops the heartbeat, waits for in-flight writes, then nulls every progress column.</summary>
    public async Task StopAndClearAsync()
    {
        await _cts.CancelAsync();
        try { if (_heartbeat is not null) await _heartbeat; } catch { /* already logged */ }
        try { await _lastWrite; } catch { /* already logged */ }
        await WriteAsync(j =>
        {
            j.ProgressProductsSeen = null;
            j.ProgressKey = null;
            j.ProgressOffset = null;
            j.ProgressTotal = null;
            j.ProgressKeysDone = null;
            j.ProgressKeysTotal = null;
            j.ProgressPagesDone = null;
            j.ProgressKeyPages = null;
            j.ProgressKeyPagesTotal = null;
            j.ProgressDoneKeys = null;
            j.LastProgressAt = null;
            j.LastHeartbeatAt = null;
        });
    }

    private async Task WriteAsync(Action<Savvori.Shared.ScrapingJob> apply)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SavvoriDbContext>();
            var job = await db.ScrapingJobs.FirstOrDefaultAsync(j => j.Id == _jobId, CancellationToken.None);
            if (job is null) return;
            apply(job);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Progress write failed for job {JobId}; ignored", _jobId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _cts.Dispose();
    }
}
