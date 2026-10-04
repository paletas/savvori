using Savvori.Shared;
using System.Net;
using System.Text;
using Savvori.WebApi.Modeling;

namespace Savvori.Web.Tests.Modeling;

/// <summary>Clock the test moves by hand, so cool-downs and backoff need no sleeping.</summary>
public sealed class ManualTimeProvider(DateTimeOffset? start = null) : TimeProvider
{
    private DateTimeOffset _now = start ?? new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Deterministic embedder: the vector depends only on the text, so equal texts give equal vectors.</summary>
public sealed class FakeEmbeddingClient : IEmbeddingClient
{
    public string ModelName { get; set; } = "fake-embed";
    public string ModelDigest { get; set; } = "digest-1";
    public int Dimension { get; set; } = 8;
    public int Calls { get; private set; }

    public Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        Calls++;
        var vectors = texts.Select(Vector).ToList();
        return Task.FromResult(new EmbeddingResult(vectors, ModelName, ModelDigest, Dimension));
    }

    public Task PingAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<ModelInfo> GetModelInfoAsync(CancellationToken ct = default) =>
        Task.FromResult(new ModelInfo(ModelName, ModelDigest));

    public float[] Vector(string text)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var v = new float[Dimension];
        for (var i = 0; i < Dimension; i++) v[i] = hash[i % hash.Length] / 255f - 0.5f;
        return v;
    }
}

public sealed class FakePairJudge(JudgeVerdict verdict = JudgeVerdict.Yes) : IPairJudge
{
    public JudgeVerdict Verdict { get; set; } = verdict;
    public Task<JudgeVerdict> JudgeAsync(JudgeItem a, JudgeItem b, CancellationToken ct = default) =>
        Task.FromResult(Verdict);
}

public sealed class FakeCategoryJudge(JudgeVerdict verdict = JudgeVerdict.Yes) : ICategoryJudge
{
    public JudgeVerdict Verdict { get; set; } = verdict;
    public Task<JudgeVerdict> JudgeAsync(CategoryJudgeItem item, CancellationToken ct = default) =>
        Task.FromResult(Verdict);
}

public enum FaultMode { None, Timeout, ServerError, BadResponse }

/// <summary>
/// Shared switchboard for the flaky wrappers: fail always, fail until a moment in (fake) time
/// ("down for a while"), or answer slowly. Counts every call that reached the wrapper.
/// </summary>
public sealed class FaultPlan(TimeProvider time)
{
    public FaultMode Mode { get; set; } = FaultMode.None;
    /// <summary>When set, <see cref="Mode"/> only applies until this instant.</summary>
    public DateTimeOffset? DownUntil { get; set; }
    public TimeSpan Delay { get; set; }
    public int Calls { get; private set; }

    public void GoDown(FaultMode mode = FaultMode.ServerError, TimeSpan? forHowLong = null)
    {
        Mode = mode;
        DownUntil = forHowLong is null ? null : time.GetUtcNow() + forHowLong;
    }

    public void Recover() { Mode = FaultMode.None; DownUntil = null; }

    public async Task ApplyAsync(CancellationToken ct)
    {
        Calls++;
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        if (Mode == FaultMode.None) return;
        if (DownUntil is { } until && time.GetUtcNow() >= until) return;
        throw Mode switch
        {
            FaultMode.Timeout => new ModelUnavailableException("simulated timeout"),
            FaultMode.ServerError => new ModelUnavailableException("simulated 503"),
            _ => new ModelResponseException("simulated malformed response")
        };
    }
}

public sealed class FlakyEmbeddingClient(IEmbeddingClient inner, FaultPlan plan) : IEmbeddingClient
{
    public async Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        await plan.ApplyAsync(ct);
        return await inner.EmbedAsync(texts, ct);
    }

    public async Task PingAsync(CancellationToken ct = default)
    {
        await plan.ApplyAsync(ct);
        await inner.PingAsync(ct);
    }

    public async Task<ModelInfo> GetModelInfoAsync(CancellationToken ct = default)
    {
        await plan.ApplyAsync(ct);
        return await inner.GetModelInfoAsync(ct);
    }
}

public sealed class FlakyPairJudge(IPairJudge inner, FaultPlan plan) : IPairJudge
{
    public async Task<JudgeVerdict> JudgeAsync(JudgeItem a, JudgeItem b, CancellationToken ct = default)
    {
        await plan.ApplyAsync(ct);
        return await inner.JudgeAsync(a, b, ct);
    }
}

public sealed class FlakyCategoryJudge(ICategoryJudge inner, FaultPlan plan) : ICategoryJudge
{
    public async Task<JudgeVerdict> JudgeAsync(CategoryJudgeItem item, CancellationToken ct = default)
    {
        await plan.ApplyAsync(ct);
        return await inner.JudgeAsync(item, ct);
    }
}

/// <summary>HttpMessageHandler driven by a lambda, for testing the Ollama clients without a server.</summary>
public sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpMethod Method, string Path, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request.Method, request.RequestUri!.AbsolutePath, body));
        return respond(request);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
