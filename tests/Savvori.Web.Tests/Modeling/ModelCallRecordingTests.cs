using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Savvori.Shared;
using Savvori.WebApi;
using Savvori.WebApi.Modeling;

namespace Savvori.Web.Tests.Modeling;

/// <summary>A real <see cref="ModelCallRecorder"/> over an in-memory SQLite database, with a listener on its own meter.</summary>
public sealed class RecorderRig : IDisposable
{
    public record Measurement(string Name, double Value, Dictionary<string, object?> Tags);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly MeterListener _listener = new();
    public ManualTimeProvider Time { get; } = new();
    public ModelOptions Options { get; } = new() { Enabled = true };
    public ModelTelemetry Telemetry { get; } = new();
    public ServiceProvider Services { get; }
    public ModelCallRecorder Recorder { get; }
    public List<Measurement> Measurements { get; } = [];

    public RecorderRig()
    {
        _connection.Open();
        var s = new ServiceCollection();
        s.AddDbContext<SavvoriDbContext>(o => o.UseSqlite(_connection));
        Services = s.BuildServiceProvider();
        using (var scope = Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<SavvoriDbContext>().Database.EnsureCreated();
        Recorder = new ModelCallRecorder(
            Services.GetRequiredService<IServiceScopeFactory>(), Telemetry, Time, NullLogger<ModelCallRecorder>.Instance);

        // Other tests create their own ModelTelemetry in parallel, so only listen to this instance's meter.
        var meter = (Meter)typeof(ModelTelemetry).GetField("_meter", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Telemetry)!;
        _listener.InstrumentPublished = (inst, l) =>
        {
            if (ReferenceEquals(inst.Meter, meter)) l.EnableMeasurementEvents(inst);
        };
        _listener.SetMeasurementEventCallback<long>((i, v, t, _) => Measurements.Add(new(i.Name, v, ToDict(t))));
        _listener.SetMeasurementEventCallback<double>((i, v, t, _) => Measurements.Add(new(i.Name, v, ToDict(t))));
        _listener.Start();
    }

    private static Dictionary<string, object?> ToDict(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var d = new Dictionary<string, object?>();
        foreach (var t in tags) d[t.Key] = t.Value;
        return d;
    }

    public List<ModelCall> Rows()
    {
        using var scope = Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<SavvoriDbContext>().ModelCalls.AsNoTracking().OrderBy(c => c.At).ToList();
    }

    public void Seed(params ModelCall[] rows)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SavvoriDbContext>();
        db.ModelCalls.AddRange(rows);
        db.SaveChanges();
    }

    public ModelStatusService StatusService(SavvoriDbContext db, ModelBreakers? breakers = null) =>
        new(db, breakers ?? new ModelBreakers(Microsoft.Extensions.Options.Options.Create(Options), Time),
            new NullStaleEmbeddingSource(), Microsoft.Extensions.Options.Options.Create(Options), Time);

    public void Dispose()
    {
        _listener.Dispose();
        Services.Dispose();
        _connection.Dispose();
        Telemetry.Dispose();
    }
}

public sealed class ModelCallRecordingTests : IDisposable
{
    private readonly RecorderRig _rig = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _rig.Dispose();

    private const string JevReply =
        """{"model":"typesafe/jev-1.13-20260917","answers":{"same":{"type":"noul","noul":0.95}},"usage":{"input_tokens":381,"output_tokens":20,"cost":0.0000167},"id":"gen-dec-1"}""";

    private static readonly JudgeItem ItemA = new("Leite Mimosa 1L", "Mimosa", "1L", "continente");
    private static readonly JudgeItem ItemB = new("Leite Meio Gordo Mimosa 1L", "Mimosa", "1L", "auchan");

    private (StubHttpHandler Handler, HttpClient Http, GatewayThrottle Throttle) Gateway(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHttpHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://gateway.test/v1/") };
        var throttle = new GatewayThrottle(Options.Create(_rig.Options), _rig.Time, (_, _) => Task.CompletedTask);
        return (handler, http, throttle);
    }

    private GatewayEmbeddingClient Embedder(Func<HttpRequestMessage, HttpResponseMessage> respond, IModelCallRecorder? recorder = null)
    {
        var (_, http, throttle) = Gateway(respond);
        return new GatewayEmbeddingClient(http, Options.Create(_rig.Options), throttle, _rig.Time, recorder ?? _rig.Recorder);
    }

    private static string EmbedBody(int count) =>
        "{\"data\":[" + string.Join(",", Enumerable.Range(0, count).Select(_ =>
            "{\"embedding\":[" + string.Join(",", Enumerable.Repeat("0.5", 1024)) + "]}")) + "]," +
        "\"usage\":{\"prompt_tokens\":42,\"total_tokens\":42}}";

    private static HttpResponseMessage Error(HttpStatusCode status, string? code = null)
    {
        var body = code is null ? "{\"error\":{\"message\":\"m\"}}" : "{\"error\":{\"message\":\"m\",\"code\":\"" + code + "\"}}";
        return StubHttpHandler.Json(body, status);
    }

    [Fact]
    public async Task JevReply_WithUsageCost_RecordsRowAndStatusSpend()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(JevReply));
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://openrouter.test/") };
        var judge = new JevPairJudge(http, Options.Create(_rig.Options), _rig.Time, _rig.Recorder);

        await judge.JudgeAsync(ItemA, ItemB, Ct);

        var row = Assert.Single(_rig.Rows());
        Assert.Equal("openrouter", row.Backend);
        Assert.Equal("pair", row.Task);
        Assert.Equal(ModelCallStatus.Ok, row.Status);
        Assert.Equal(200, row.HttpStatus);
        Assert.Equal(381, row.PromptTokens);
        Assert.Equal(20, row.CompletionTokens);
        Assert.Equal(0.0000167m, row.CostUsd);
        Assert.Equal("typesafe/jev-1.13-20260917", row.Model);
        Assert.Equal(1, row.Items);

        using var scope = _rig.Services.CreateScope();
        var status = await _rig.StatusService(scope.ServiceProvider.GetRequiredService<SavvoriDbContext>()).GetAsync(Ct);
        Assert.Equal(0.0000167m, status.SpendTodayUsd);
        Assert.Equal(0.0000167m, status.Spend30dUsd);
        var task = Assert.Single(status.SpendByTask);
        Assert.Equal("pair", task.Task);
        Assert.Equal(1, task.Calls30d);

        var cost = Assert.Single(_rig.Measurements, m => m.Name == "savvori.model.cost");
        Assert.Equal(0.0000167, cost.Value, 1e-12);
        Assert.Equal("openrouter", cost.Tags["backend"]);
        Assert.Equal("pair", cost.Tags["task"]);
    }

    [Fact]
    public async Task Embeddings_RecordItemsAsBatchSize_CostZero()
    {
        var embedder = Embedder(_ => StubHttpHandler.Json(EmbedBody(3)));

        await embedder.EmbedAsync(["a", "b", "c"], Ct);

        var row = Assert.Single(_rig.Rows());
        Assert.Equal("embed", row.Task);
        Assert.Equal(3, row.Items);
        Assert.Equal(0m, row.CostUsd);
        Assert.Equal("gateway", row.Backend);
        Assert.Equal(42, row.PromptTokens);
    }

    [Fact]
    public async Task ThrottledAndRetried_RecordsOneRowPerAttempt()
    {
        var calls = 0;
        var embedder = Embedder(_ =>
        {
            if (calls++ > 0) return StubHttpHandler.Json(EmbedBody(1));
            var r = Error((HttpStatusCode)429);
            r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return r;
        });

        await embedder.EmbedAsync(["a"], Ct);

        var rows = _rig.Rows();
        Assert.Equal(2, rows.Count);
        Assert.Equal(ModelCallStatus.Throttled, rows[0].Status);
        Assert.Equal(429, rows[0].HttpStatus);
        Assert.Equal(ModelCallStatus.Ok, rows[1].Status);
        Assert.Equal(1, _rig.Measurements.Count(m => m.Name == "savvori.model.throttled"));
    }

    [Fact]
    public async Task Quota429_RecordsThrottled_NoOkRow()
    {
        var embedder = Embedder(_ => Error((HttpStatusCode)429, "quota_exceeded"));

        await Assert.ThrowsAsync<ModelThrottledException>(() => embedder.EmbedAsync(["a"], Ct));

        var row = Assert.Single(_rig.Rows());
        Assert.Equal(ModelCallStatus.Throttled, row.Status);
        Assert.Equal(429, row.HttpStatus);
    }

    [Fact]
    public async Task ServerError_RecordsUnavailable()
    {
        var embedder = Embedder(_ => Error(HttpStatusCode.ServiceUnavailable, "upstream_unhealthy"));

        await Assert.ThrowsAsync<ModelUnavailableException>(() => embedder.EmbedAsync(["a"], Ct));

        var row = Assert.Single(_rig.Rows());
        Assert.Equal(ModelCallStatus.Unavailable, row.Status);
        Assert.Equal(503, row.HttpStatus);
    }

    [Fact]
    public async Task Timeout_RecordsUnavailable_NoHttpStatus()
    {
        var embedder = Embedder(_ => throw new TaskCanceledException("t"));

        await Assert.ThrowsAsync<ModelUnavailableException>(() => embedder.EmbedAsync(["a"], Ct));

        var row = Assert.Single(_rig.Rows());
        Assert.Equal(ModelCallStatus.Unavailable, row.Status);
        Assert.Null(row.HttpStatus);
    }

    [Fact]
    public async Task MalformedBody_RecordsError()
    {
        var embedder = Embedder(_ => StubHttpHandler.Json("this is not json"));

        await Assert.ThrowsAsync<ModelResponseException>(() => embedder.EmbedAsync(["a"], Ct));

        var row = Assert.Single(_rig.Rows());
        Assert.Equal(ModelCallStatus.Error, row.Status);
    }

    [Fact]
    public async Task BadRequest400_RecordsError()
    {
        var embedder = Embedder(_ => Error(HttpStatusCode.BadRequest));

        await Assert.ThrowsAsync<ModelResponseException>(() => embedder.EmbedAsync(["a"], Ct));

        var row = Assert.Single(_rig.Rows());
        Assert.Equal(ModelCallStatus.Error, row.Status);
        Assert.Equal(400, row.HttpStatus);
    }

    [Fact]
    public async Task CategoryJudge_RecordsChatUsage_AndGatewayCostZero()
    {
        var (_, http, throttle) = Gateway(_ => StubHttpHandler.Json(
            """{"choices":[{"message":{"content":"yes"}}],"usage":{"prompt_tokens":120,"completion_tokens":1}}"""));
        var judge = new GatewayCategoryJudge(http, Options.Create(_rig.Options), throttle, _rig.Time, _rig.Recorder);

        await judge.JudgeAsync(new CategoryJudgeItem("Leite Meio Gordo", "Mimosa", null, "Leite"), Ct);

        var row = Assert.Single(_rig.Rows());
        Assert.Equal("category", row.Task);
        Assert.Equal(_rig.Options.CategoryModel, row.Model);
        Assert.Equal(120, row.PromptTokens);
        Assert.Equal(1, row.CompletionTokens);
        Assert.Equal(0m, row.CostUsd);
    }

    [Fact]
    public async Task Ping_RecordsProbeRow_Items0()
    {
        var embedder = Embedder(_ => StubHttpHandler.Json("""{"data":[]}"""));

        await embedder.PingAsync(Ct);

        var row = Assert.Single(_rig.Rows());
        Assert.Equal("probe", row.Task);
        Assert.Equal(0, row.Items);
        Assert.Equal(ModelCallStatus.Ok, row.Status);
    }

    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("db is gone");
    }

    [Fact]
    public async Task RecorderDbFailure_DoesNotFailTheCall()
    {
        var recorder = new ModelCallRecorder(
            new ThrowingScopeFactory(), _rig.Telemetry, _rig.Time, NullLogger<ModelCallRecorder>.Instance);
        var embedder = Embedder(_ => StubHttpHandler.Json(EmbedBody(1)), recorder);

        var result = await embedder.EmbedAsync(["a"], Ct);

        Assert.Single(result.Vectors);
        // The metrics do not depend on the database.
        Assert.Single(_rig.Measurements, m => m.Name == "savvori.model.calls");
    }

    [Fact]
    public void RowsNeverContainBodies()
    {
        var strings = typeof(ModelCall).GetProperties().Where(p => p.PropertyType == typeof(string)).Select(p => p.Name);

        Assert.Equal(["Backend", "Model", "Task"], strings.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Prune_DeletesRowsOlderThan90Days_AtMostOncePer24h()
    {
        ModelCall Row(int daysAgo) => new()
        {
            Id = Guid.NewGuid(), At = _rig.Time.GetUtcNow().UtcDateTime.AddDays(-daysAgo),
            Backend = "gateway", Task = "embed", Model = "m", Status = ModelCallStatus.Ok
        };
        var old = Row(91);
        var recent = Row(89);
        _rig.Seed(old, recent);

        await _rig.Recorder.PruneIfDueAsync(Ct);

        Assert.Equal([recent.Id], _rig.Rows().Select(r => r.Id).ToArray());

        var older = Row(95);
        _rig.Seed(older);
        await _rig.Recorder.PruneIfDueAsync(Ct);
        Assert.Contains(_rig.Rows(), r => r.Id == older.Id);

        _rig.Time.Advance(TimeSpan.FromHours(25));
        await _rig.Recorder.PruneIfDueAsync(Ct);
        Assert.DoesNotContain(_rig.Rows(), r => r.Id == older.Id);
    }

    [Fact]
    public async Task DeferralCounter_IncrementedOnThrottledDrain()
    {
        using var h = new PipelineHost();
        var (a, _) = h.AddListed(h.ChainA, "Iogurte Grego Natural");
        var (b, _) = h.AddListed(h.ChainB, "Iogurte Grego");
        h.AddCandidate(a, b, 0.85);
        h.Judge.Verdict = JudgeVerdict.Yes;
        await h.RunMatchingAsync();
        await h.ScanAsync();
        h.JudgeFaults.GoDown(FaultMode.Throttled);
        var telemetry = h.Services.GetRequiredService<ModelTelemetry>();
        var meter = (Meter)typeof(ModelTelemetry).GetField("_meter", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(telemetry)!;
        var seen = new List<(long Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (inst, l) =>
        {
            if (ReferenceEquals(inst.Meter, meter) && inst.Name == "savvori.model.deferred") l.EnableMeasurementEvents(inst);
        };
        listener.SetMeasurementEventCallback<long>((_, v, t, _) =>
        {
            var d = new Dictionary<string, object?>();
            foreach (var tag in t) d[tag.Key] = tag.Value;
            seen.Add((v, d));
        });
        listener.Start();

        await h.DrainAsync();

        var m = Assert.Single(seen);
        Assert.Equal(1, m.Value);
        Assert.Equal("openrouter", m.Tags["backend"]);
    }
}
