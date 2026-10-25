using System.Globalization;
using Microsoft.Extensions.Options;

namespace Savvori.WebApi.Modeling;

/// <summary>
/// One shared client-side limiter for every gateway caller (embeddings, category judge, translator, health probe).
/// The gateway allows 120 requests per source IP in a fixed 60 s window and counts everything (probes, 429s, retries),
/// so every send takes one slot here. Our local count is corrected from the <c>X-RateLimit-*</c> response headers,
/// which also reflects other hosts behind the same IP. At most <see cref="GatewayThrottleOptions.MaxInFlight"/>
/// requests are in flight at once.
/// </summary>
public sealed class GatewayThrottle
{
    private readonly IOptions<ModelOptions> _options;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _inflight;
    private readonly object _lock = new();
    private DateTimeOffset? _windowEnd;
    private int _count;
    private int? _remaining;

    public GatewayThrottle(IOptions<ModelOptions> options, TimeProvider time,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _options = options;
        _time = time;
        _delay = delay ?? ((t, ct) => Task.Delay(t, time, ct));
        _inflight = new SemaphoreSlim(Math.Max(1, Options.MaxInFlight));
    }

    public GatewayThrottleOptions Options => _options.Value.Gateway.Throttle;

    /// <summary>Requests counted in the current window (for tests).</summary>
    public int WindowCount
    {
        get { lock (_lock) return _count; }
    }

    public Task SleepAsync(TimeSpan d, CancellationToken ct) => _delay(d, ct);

    /// <summary>Waits for an in-flight slot and a window slot. Dispose the lease as soon as the response has arrived.</summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        await _inflight.WaitAsync(ct);
        try
        {
            while (true)
            {
                TimeSpan wait;
                lock (_lock)
                {
                    var o = Options;
                    var now = _time.GetUtcNow();
                    if (_windowEnd is null || now >= _windowEnd)
                    {
                        _windowEnd = now + TimeSpan.FromSeconds(o.WindowSeconds);
                        _count = 0;
                        _remaining = null;
                    }
                    if (_count >= o.WindowRequests || _remaining <= o.LowWater)
                        wait = _windowEnd.Value - now;
                    else
                    {
                        _count++;
                        return new Lease(_inflight);
                    }
                }
                await SleepAsync(wait, ct);
            }
        }
        catch
        {
            _inflight.Release();
            throw;
        }
    }

    /// <summary>Corrects the local window from the server's rate-limit headers (also on error responses).</summary>
    public void Observe(HttpResponseMessage response)
    {
        if (!TryInt(response, "X-RateLimit-Remaining", out var remaining) ||
            !TryLong(response, "X-RateLimit-Reset", out var reset)) return;
        int? limit = TryInt(response, "X-RateLimit-Limit", out var l) ? l : null;

        var resetAt = DateTimeOffset.FromUnixTimeSeconds(reset);
        lock (_lock)
        {
            if (resetAt <= _time.GetUtcNow()) return; // stale window
            if (resetAt != _windowEnd)
            {
                // The server's window differs from ours (or we have none yet): adopt it.
                _windowEnd = resetAt;
                _remaining = remaining;
                _count = limit is { } lim ? Math.Max(1, lim - remaining) : Math.Max(1, _count);
            }
            else
            {
                _remaining = Math.Min(_remaining ?? remaining, remaining);
                if (limit is { } lim2) _count = Math.Max(_count, lim2 - remaining);
            }
        }
    }

    private static bool TryInt(HttpResponseMessage r, string name, out int value)
    {
        value = 0;
        return r.Headers.TryGetValues(name, out var v) &&
               int.TryParse(v.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryLong(HttpResponseMessage r, string name, out long value)
    {
        value = 0;
        return r.Headers.TryGetValues(name, out var v) &&
               long.TryParse(v.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) semaphore.Release();
        }
    }
}
