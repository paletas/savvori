using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Savvori.WebApi.Modeling;

namespace Savvori.Web.Tests.Modeling;

public sealed class ModelHttpTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Harness
    {
        public ManualTimeProvider Time { get; } = new();
        public List<TimeSpan> Delays { get; } = [];
        public Queue<Func<HttpResponseMessage>> Responses { get; } = new();
        public GatewayThrottle Throttle { get; }
        public StubHttpHandler Handler { get; }
        public HttpClient Http { get; }

        public Harness()
        {
            var o = new ModelOptions
            {
                Gateway =
                {
                    Throttle = { WindowRequests = 3, LowWater = 2, MaxInFlight = 2, ProxyRetryMax = 2, PaddingMs = 0 }
                }
            };
            Throttle = new GatewayThrottle(Options.Create(o), Time, (t, _) =>
            {
                Delays.Add(t);
                Time.Advance(t);
                return Task.CompletedTask;
            });
            Handler = new StubHttpHandler(_ => Responses.Count > 0 ? Responses.Dequeue()() : Ok());
            Http = new HttpClient(Handler) { BaseAddress = new Uri("http://gateway.test/v1/") };
        }

        public Harness Enqueue(params Func<HttpResponseMessage>[] responses)
        {
            foreach (var r in responses) Responses.Enqueue(r);
            return this;
        }

        public Task<HttpResponseMessage> Send(bool useThrottle = true) =>
            ModelHttp.SendAsync(Http, () => new HttpRequestMessage(HttpMethod.Get, "models"),
                useThrottle ? Throttle : null, Time, Ct);

        public long InMs(TimeSpan d) => Time.GetUtcNow().Add(d).ToUnixTimeMilliseconds();

        public Func<HttpResponseMessage> OkWithLimits(int remaining, int resetInSeconds, int limit = 120) => () =>
        {
            var r = Ok();
            r.Headers.Add("X-RateLimit-Limit", limit.ToString());
            r.Headers.Add("X-RateLimit-Remaining", remaining.ToString());
            r.Headers.Add("X-RateLimit-Reset", Time.GetUtcNow().AddSeconds(resetInSeconds).ToUnixTimeSeconds().ToString());
            return r;
        };
    }

    private static HttpResponseMessage Ok() => StubHttpHandler.Json("""{"data":[]}""");

    private static Func<HttpResponseMessage> Error(HttpStatusCode status, string? code = null, long? retryAtMs = null,
        int? retryAfterSeconds = null) => () =>
    {
        var fields = "\"message\":\"m\",\"type\":\"rate_limit_error\"";
        if (code is not null) fields += $",\"code\":\"{code}\"";
        if (retryAtMs is not null) fields += $",\"retryAtMs\":{retryAtMs}";
        var r = StubHttpHandler.Json("{\"error\":{" + fields + "}}", status);
        if (retryAfterSeconds is { } s) r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(s));
        return r;
    };

    private static Func<HttpResponseMessage> Proxy429(int? retryAfterSeconds = null) =>
        Error((HttpStatusCode)429, retryAfterSeconds: retryAfterSeconds);

    [Fact]
    public async Task Window_FourthCallWaitsForWindowEnd()
    {
        var h = new Harness();
        for (var i = 0; i < 4; i++) (await h.Send()).Dispose();

        Assert.Equal(4, h.Handler.Requests.Count);
        Assert.Equal(TimeSpan.FromSeconds(60), Assert.Single(h.Delays));
    }

    [Fact]
    public async Task LowRemaining_SleepsUntilServerReset_NotFullWindow()
    {
        var h = new Harness();
        h.Enqueue(h.OkWithLimits(1, 20));
        (await h.Send()).Dispose();
        (await h.Send()).Dispose();

        Assert.Equal(TimeSpan.FromSeconds(20), Assert.Single(h.Delays));
        Assert.Equal(2, h.Handler.Requests.Count);
    }

    [Fact]
    public async Task EverySendCounts()
    {
        var h = new Harness().Enqueue(Proxy429(7), () => Ok());
        (await h.Send()).Dispose();   // 429, sleep, retry: 2 sends
        (await h.Send()).Dispose();   // a probe-like GET models: 3rd send

        Assert.Equal(3, h.Throttle.WindowCount);
        Assert.Equal(3, h.Handler.Requests.Count);
    }

    [Fact]
    public async Task MaxInFlight_IsRespected()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var current = 0;
        var peak = 0;
        var handler = new BlockingHandler(async () =>
        {
            Interlocked.Increment(ref entered);
            var now = Interlocked.Increment(ref current);
            int p;
            while (now > (p = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, p) != p) { }
            await gate.Task;
            Interlocked.Decrement(ref current);
        });
        var time = new ManualTimeProvider();
        var throttle = new GatewayThrottle(
            Options.Create(new ModelOptions { Gateway = { Throttle = { WindowRequests = 10, MaxInFlight = 2 } } }),
            time, (_, _) => Task.CompletedTask);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://gateway.test/v1/") };

        var calls = Enumerable.Range(0, 3).Select(_ => ModelHttp.SendAsync(
            http, () => new HttpRequestMessage(HttpMethod.Get, "models"), throttle, time, Ct)).ToList();

        for (var i = 0; i < 200 && Volatile.Read(ref entered) < 2; i++) await Task.Delay(10, Ct);
        await Task.Delay(50, Ct); // give a (wrongly admitted) third call the chance to show up
        Assert.Equal(2, Volatile.Read(ref entered));

        gate.SetResult();
        foreach (var r in await Task.WhenAll(calls)) r.Dispose();
        Assert.Equal(3, Volatile.Read(ref entered));
        Assert.Equal(2, Volatile.Read(ref peak));
    }

    private sealed class BlockingHandler(Func<Task> onSend) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await onSend();
            return Ok();
        }
    }

    [Fact]
    public async Task ProxyLimiter429_SleepsRetryAfterAndRetries()
    {
        var h = new Harness().Enqueue(Proxy429(7), () => Ok());

        (await h.Send()).Dispose();

        Assert.Equal(TimeSpan.FromSeconds(7), Assert.Single(h.Delays));
        Assert.Equal(2, h.Handler.Requests.Count);
    }

    [Fact]
    public async Task ProxyLimiter429_Exhausted_ThrowsThrottledProxy()
    {
        var h = new Harness().Enqueue(Proxy429(5), Proxy429(5), Proxy429(5));

        var ex = await Assert.ThrowsAsync<ModelThrottledException>(() => h.Send());

        Assert.Equal(ThrottleKind.Proxy, ex.Kind);
        Assert.Equal(TimeSpan.FromSeconds(5), ex.RetryAfter);
        Assert.Equal(3, h.Handler.Requests.Count);
        Assert.Equal(2, h.Delays.Count);
    }

    [Fact]
    public async Task Routing429_UsesLargerOfRetryAfterAndRetryAtMs_NoRetry()
    {
        var h = new Harness();
        h.Enqueue(Error((HttpStatusCode)429, "rate_limit_exceeded", h.InMs(TimeSpan.FromSeconds(300)), 120));

        var ex = await Assert.ThrowsAsync<ModelThrottledException>(() => h.Send());

        Assert.Equal(ThrottleKind.Routing, ex.Kind);
        Assert.Equal(TimeSpan.FromSeconds(300), ex.RetryAfter);
        Assert.Single(h.Handler.Requests);
        Assert.Empty(h.Delays);
    }

    [Fact]
    public async Task Routing429_WithoutHints_Defaults60s()
    {
        var h = new Harness().Enqueue(Error((HttpStatusCode)429, "rate_limit_exceeded"));

        var ex = await Assert.ThrowsAsync<ModelThrottledException>(() => h.Send());

        Assert.Equal(ThrottleKind.Routing, ex.Kind);
        Assert.Equal(TimeSpan.FromSeconds(60), ex.RetryAfter);
        Assert.Single(h.Handler.Requests);
    }

    [Fact]
    public async Task Routing429_ClampedTo24h()
    {
        var h = new Harness();
        h.Enqueue(Error((HttpStatusCode)429, "rate_limit_exceeded", h.InMs(TimeSpan.FromDays(3))));

        var ex = await Assert.ThrowsAsync<ModelThrottledException>(() => h.Send());

        Assert.Equal(ThrottleKind.Routing, ex.Kind);
        Assert.Equal(TimeSpan.FromHours(24), ex.RetryAfter);
    }

    [Fact]
    public async Task QuotaExceeded_LongDefer()
    {
        var h = new Harness();
        h.Enqueue(Error((HttpStatusCode)429, "quota_exceeded", h.InMs(TimeSpan.FromDays(10))));

        var ex = await Assert.ThrowsAsync<ModelThrottledException>(() => h.Send());

        Assert.Equal(ThrottleKind.Quota, ex.Kind);
        Assert.Equal(TimeSpan.FromDays(10), ex.RetryAfter);
        Assert.Single(h.Handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, "upstream_failed")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "upstream_unhealthy")]
    [InlineData(HttpStatusCode.InternalServerError, "x")]
    [InlineData(HttpStatusCode.NotFound, "x")]
    public async Task Gateway502UpstreamFailedAnd503_AreUnavailable(HttpStatusCode status, string code)
    {
        var h = new Harness().Enqueue(Error(status, code));

        var ex = await Assert.ThrowsAsync<ModelUnavailableException>(() => h.Send());

        if (status is HttpStatusCode.BadGateway) Assert.Contains("upstream_failed", ex.Message);
        Assert.Single(h.Handler.Requests);
    }

    [Fact]
    public async Task TransportErrorsAndTimeouts_AreUnavailable()
    {
        var h = new Harness().Enqueue(
            () => throw new HttpRequestException("refused"), () => throw new TaskCanceledException("t"));

        await Assert.ThrowsAsync<ModelUnavailableException>(() => h.Send());
        await Assert.ThrowsAsync<ModelUnavailableException>(() => h.Send());
    }

    [Fact]
    public async Task OtherClientErrors_AreResponseErrors()
    {
        var h = new Harness().Enqueue(
            Error(HttpStatusCode.BadRequest, "x"), Error((HttpStatusCode)429, "routing_exhausted"));

        await Assert.ThrowsAsync<ModelResponseException>(() => h.Send());
        var ex = await Assert.ThrowsAsync<ModelResponseException>(() => h.Send());
        Assert.Contains("routing_exhausted", ex.Message);
        Assert.Empty(h.Delays);
    }

    [Fact]
    public async Task NullThrottle_429_ThrowsRoutingWithoutRetry()
    {
        var h = new Harness().Enqueue(Proxy429(30), () => Ok());

        var ex = await Assert.ThrowsAsync<ModelThrottledException>(() => h.Send(useThrottle: false));

        Assert.Equal(TimeSpan.FromSeconds(30), ex.RetryAfter);
        Assert.Single(h.Handler.Requests);
        Assert.Empty(h.Delays);
        Assert.Equal(0, h.Throttle.WindowCount);
    }

    [Fact]
    public void ParseRetryAfter_Seconds_HttpDate_Absent()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        using var seconds = new HttpResponseMessage();
        seconds.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(12));
        Assert.Equal(TimeSpan.FromSeconds(12), ModelHttp.ParseRetryAfter(seconds, now));

        using var date = new HttpResponseMessage();
        date.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddSeconds(90));
        Assert.Equal(TimeSpan.FromSeconds(90), ModelHttp.ParseRetryAfter(date, now));

        using var past = new HttpResponseMessage();
        past.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddSeconds(-90));
        Assert.Equal(TimeSpan.Zero, ModelHttp.ParseRetryAfter(past, now));

        using var absent = new HttpResponseMessage();
        Assert.Null(ModelHttp.ParseRetryAfter(absent, now));
    }

    private sealed class ThrowingEmbedder(Exception ex) : IEmbeddingClient
    {
        public Task<ModelInfo> GetModelInfoAsync(CancellationToken ct = default) => throw ex;
        public Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default) => throw ex;
        public Task PingAsync(CancellationToken ct = default) => throw ex;
    }

    [Fact]
    public async Task ThrottledCall_IsNotABreakerFailure()
    {
        var breaker = new ModelCircuitBreaker(
            Options.Create(new ModelOptions { Breaker = { FailureThreshold = 1 } }), new ManualTimeProvider());
        var thrown = new ModelThrottledException(TimeSpan.FromMinutes(5), ThrottleKind.Routing, "throttled");
        var client = new BreakerEmbeddingClient(new ThrowingEmbedder(thrown), breaker);

        var ex = await Assert.ThrowsAsync<ModelThrottledException>(() => client.EmbedAsync(["a"], Ct));

        Assert.Same(thrown, ex);
        var snap = breaker.Snapshot();
        Assert.Equal(0, snap.ConsecutiveFailures);
        Assert.Equal(BreakerState.Closed, snap.State);
    }
}
