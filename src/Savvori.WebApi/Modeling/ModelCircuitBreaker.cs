using Microsoft.Extensions.Options;

namespace Savvori.WebApi.Modeling;

public enum BreakerState { Closed, Open, HalfOpen }

public sealed record BreakerSnapshot(
    BreakerState State, int ConsecutiveFailures, DateTime? LastSuccessAt,
    DateTime? LastErrorAt, string? LastError, DateTime? RetryAt);

/// <summary>
/// Stops all model calls after N consecutive failures for a cool-down, then lets exactly one probe through.
/// A successful probe closes it; a failed one re-opens it. In-memory: after a restart it starts Closed
/// and re-opens after N failures.
/// </summary>
public sealed class ModelCircuitBreaker(IOptions<ModelOptions> options, TimeProvider time)
{
    private readonly object _gate = new();
    private BreakerState _state = BreakerState.Closed;
    private int _failures;
    private DateTime? _openedAt;
    private DateTime? _lastSuccess;
    private DateTime? _lastErrorAt;
    private string? _lastError;

    private TimeSpan Cooldown => TimeSpan.FromSeconds(options.Value.Breaker.CooldownSeconds);
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public BreakerSnapshot Snapshot()
    {
        lock (_gate)
            return new(_state, _failures, _lastSuccess, _lastErrorAt, _lastError,
                _state == BreakerState.Closed ? null : _openedAt + Cooldown);
    }

    public bool IsClosed { get { lock (_gate) return _state == BreakerState.Closed; } }

    /// <summary>
    /// True if a call may be made now. Closed: always. Open: only once the cool-down has elapsed, and then for a
    /// single caller (the probe); everyone else is refused until that probe reports back.
    /// </summary>
    public bool TryAcquire()
    {
        lock (_gate)
        {
            switch (_state)
            {
                case BreakerState.Closed:
                    return true;
                // A probe that never reported back (cancelled, crashed) must not wedge the breaker:
                // once a further cool-down passes, another probe is allowed.
                case BreakerState.Open or BreakerState.HalfOpen when Now >= _openedAt + Cooldown:
                    _state = BreakerState.HalfOpen;
                    _openedAt = Now;
                    return true;
                default:
                    return false;
            }
        }
    }

    public void RecordSuccess()
    {
        lock (_gate)
        {
            _state = BreakerState.Closed;
            _failures = 0;
            _openedAt = null;
            _lastSuccess = Now;
        }
    }

    public void RecordFailure(string error)
    {
        lock (_gate)
        {
            _failures++;
            _lastErrorAt = Now;
            _lastError = error;
            if (_state == BreakerState.HalfOpen || _failures >= options.Value.Breaker.FailureThreshold)
            {
                _state = BreakerState.Open;
                _openedAt = Now;
            }
        }
    }
}
