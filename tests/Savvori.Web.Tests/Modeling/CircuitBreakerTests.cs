using Microsoft.Extensions.Options;
using Savvori.WebApi.Modeling;

namespace Savvori.Web.Tests.Modeling;

public sealed class CircuitBreakerTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly ModelOptions _opts = new()
    {
        Breaker = { FailureThreshold = 3, CooldownSeconds = 60 }
    };

    private ModelCircuitBreaker NewBreaker() => new(Options.Create(_opts), _time);

    [Fact]
    public void StaysClosed_BelowThreshold_AndSuccessResetsCount()
    {
        var b = NewBreaker();
        b.RecordFailure("x");
        b.RecordFailure("x");
        b.RecordSuccess();
        b.RecordFailure("x");
        b.RecordFailure("x");

        Assert.True(b.IsClosed);
        Assert.Equal(2, b.Snapshot().ConsecutiveFailures);
    }

    [Fact]
    public void Opens_AfterConsecutiveFailures_AndRefusesCallsDuringCooldown()
    {
        var b = NewBreaker();
        for (var i = 0; i < 3; i++) b.RecordFailure("boom");

        Assert.Equal(BreakerState.Open, b.Snapshot().State);
        Assert.False(b.TryAcquire());
        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.False(b.TryAcquire());
    }

    [Fact]
    public void AfterCooldown_AllowsExactlyOneProbe_AndSuccessCloses()
    {
        var b = NewBreaker();
        for (var i = 0; i < 3; i++) b.RecordFailure("boom");
        _time.Advance(TimeSpan.FromSeconds(60));

        Assert.True(b.TryAcquire());   // the probe
        Assert.False(b.TryAcquire());  // everyone else still refused while it is in flight
        Assert.Equal(BreakerState.HalfOpen, b.Snapshot().State);

        b.RecordSuccess();
        Assert.True(b.IsClosed);
        Assert.True(b.TryAcquire());
        Assert.NotNull(b.Snapshot().LastSuccessAt);
    }

    [Fact]
    public void FailedProbe_ReopensForAnotherFullCooldown()
    {
        var b = NewBreaker();
        for (var i = 0; i < 3; i++) b.RecordFailure("boom");
        _time.Advance(TimeSpan.FromSeconds(60));
        Assert.True(b.TryAcquire());

        b.RecordFailure("still down");

        Assert.Equal(BreakerState.Open, b.Snapshot().State);
        Assert.False(b.TryAcquire());
        _time.Advance(TimeSpan.FromSeconds(60));
        Assert.True(b.TryAcquire());
    }

    [Fact]
    public void ProbeThatNeverReportsBack_DoesNotWedgeTheBreaker()
    {
        var b = NewBreaker();
        for (var i = 0; i < 3; i++) b.RecordFailure("boom");
        _time.Advance(TimeSpan.FromSeconds(60));
        Assert.True(b.TryAcquire()); // probe starts, then is cancelled without reporting

        Assert.False(b.TryAcquire());
        _time.Advance(TimeSpan.FromSeconds(60));
        Assert.True(b.TryAcquire());
    }

    [Fact]
    public async Task GuardedClient_OpensAfterFailures_StopsCallingInner_ThenRecoversOnProbe()
    {
        var plan = new FaultPlan(_time);
        var breaker = NewBreaker();
        var client = new BreakerEmbeddingClient(new FlakyEmbeddingClient(new FakeEmbeddingClient(), plan), breaker);
        var ct = TestContext.Current.CancellationToken;

        plan.GoDown(FaultMode.Timeout, TimeSpan.FromMinutes(10)); // "down for a while"
        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAsync<ModelUnavailableException>(() => client.EmbedAsync(["a"], ct));
        Assert.Equal(3, plan.Calls);
        Assert.Equal(BreakerState.Open, breaker.Snapshot().State);

        // Open: refused without touching the backend.
        await Assert.ThrowsAsync<ModelUnavailableException>(() => client.EmbedAsync(["a"], ct));
        await Assert.ThrowsAsync<ModelUnavailableException>(() => client.PingAsync(ct));
        Assert.Equal(3, plan.Calls);

        // Cool-down over but the model is still down: the single probe fails and re-opens.
        _time.Advance(TimeSpan.FromSeconds(60));
        await Assert.ThrowsAsync<ModelUnavailableException>(() => client.PingAsync(ct));
        Assert.Equal(4, plan.Calls);
        Assert.Equal(BreakerState.Open, breaker.Snapshot().State);

        // Model comes back; next probe after the cool-down closes the breaker automatically.
        _time.Advance(TimeSpan.FromMinutes(10));
        await client.PingAsync(ct);
        Assert.True(breaker.IsClosed);
        var result = await client.EmbedAsync(["a"], ct);
        Assert.Single(result.Vectors);
    }

    [Fact]
    public async Task MalformedResponses_DoNotOpenTheBreaker()
    {
        var plan = new FaultPlan(_time);
        var breaker = NewBreaker();
        var client = new BreakerEmbeddingClient(new FlakyEmbeddingClient(new FakeEmbeddingClient(), plan), breaker);
        plan.GoDown(FaultMode.BadResponse);

        for (var i = 0; i < 10; i++)
            await Assert.ThrowsAsync<ModelResponseException>(() => client.EmbedAsync(["a"], TestContext.Current.CancellationToken));

        Assert.True(breaker.IsClosed); // the server answered, so it is reachable
    }

    [Fact]
    public async Task SlowBackend_HonoursCallerCancellation_WithoutCountingAsFailure()
    {
        var plan = new FaultPlan(_time) { Delay = TimeSpan.FromSeconds(30) };
        var breaker = NewBreaker();
        var client = new BreakerEmbeddingClient(new FlakyEmbeddingClient(new FakeEmbeddingClient(), plan), breaker);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.EmbedAsync(["a"], cts.Token));

        Assert.Equal(0, breaker.Snapshot().ConsecutiveFailures);
    }
}
