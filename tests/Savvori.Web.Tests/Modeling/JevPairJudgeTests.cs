using Savvori.Shared;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Savvori.WebApi.Modeling;

namespace Savvori.Web.Tests.Modeling;

public sealed class JevPairJudgeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly JudgeItem A = new("Spaghetti n.5", "Barilla", "500g", "Continente");
    private static readonly JudgeItem B = new("Esparguete 5", "Barilla", "500 g", "Pingo Doce");

    // Literals copied from trials/harness/decider.py (QUESTION).
    private const string DeciderInstructions = "Are listing A and listing B the same product (same brand, variety/flavour and pack size)?";
    private const string DeciderTrue = "Same brand, same variety or flavour and the same pack size; only the store or the wording differs.";
    private const string DeciderFalse = "A different brand, variety, flavour or pack size.";

    private sealed class Rig
    {
        public ModelOptions Opts { get; } = new();
        public StubHttpHandler Handler { get; }
        public JevPairJudge Judge { get; }

        public Rig(Func<HttpRequestMessage, HttpResponseMessage> respond, double? threshold = null)
        {
            if (threshold is { } t) Opts.Matching.PairJudgeThreshold = t;
            Handler = new StubHttpHandler(respond);
            var http = new HttpClient(Handler) { BaseAddress = new Uri("https://openrouter.test/") };
            Judge = new JevPairJudge(http, Options.Create(Opts), new ManualTimeProvider());
        }
    }

    private static string Decision(string noulJson, string extra = "") =>
        "{\"model\":\"typesafe/jev-1.13-20260917\",\"answers\":{\"same\":{\"type\":\"noul\",\"noul\":" + noulJson + "}}" + extra + "}";

    private static HttpResponseMessage Ok(double noul) =>
        StubHttpHandler.Json(Decision(noul.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public async Task Body_EqualsDeciderPy()
    {
        var rig = new Rig(_ => Ok(0.9));

        await rig.Judge.JudgeAsync(A, B, Ct);

        var req = Assert.Single(rig.Handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("/api/alpha/decisions", req.Path);
        using var doc = JsonDocument.Parse(req.Body);
        var root = doc.RootElement;
        Assert.Equal("typesafe/jev-1.13", root.GetProperty("model").GetString());
        Assert.Equal("A: Spaghetti n.5 | Barilla | 500g | Continente\nB: Esparguete 5 | Barilla | 500 g | Pingo Doce",
            root.GetProperty("state").GetString());
        var same = root.GetProperty("questions").GetProperty("same");
        Assert.Equal("noul", same.GetProperty("type").GetString());
        Assert.Equal(DeciderInstructions, same.GetProperty("instructions").GetString());
        Assert.Equal(DeciderTrue, same.GetProperty("criteria").GetProperty("true").GetString());
        Assert.Equal(DeciderFalse, same.GetProperty("criteria").GetProperty("false").GetString());
    }

    [Fact]
    public void State_SkipsBlankParts_AndHasNoTrailingQuestion()
    {
        var state = JevPairJudge.BuildState(new JudgeItem("Leite", null, "", "Continente"), B);
        Assert.StartsWith("A: Leite | Continente\nB: ", state);
        Assert.DoesNotContain("Same product?", state);
    }

    [Theory]
    [InlineData(0.49, JudgeVerdict.No)]
    [InlineData(0.5, JudgeVerdict.Yes)]
    [InlineData(0.51, JudgeVerdict.Yes)]
    public async Task Threshold_MapsNoulToVerdict(double noul, JudgeVerdict expected)
    {
        var rig = new Rig(_ => Ok(noul));

        var result = await rig.Judge.JudgeAsync(A, B, Ct);

        Assert.Equal(expected, result.Verdict);
        Assert.Equal(noul, result.Score);
    }

    [Fact]
    public async Task CustomThreshold_IsHonoured()
    {
        var rig = new Rig(_ => Ok(0.7), threshold: 0.8);

        var result = await rig.Judge.JudgeAsync(A, B, Ct);

        Assert.Equal(JudgeVerdict.No, result.Verdict);
        Assert.Equal(0.7, result.Score);
    }

    [Fact]
    public async Task Usage_IsCapturedFromTheResponse()
    {
        var rig = new Rig(_ => StubHttpHandler.Json(Decision("0.95",
            ",\"usage\":{\"input_tokens\":381,\"output_tokens\":20,\"cost\":0.000016002},\"id\":\"gen-dec-1\"")));

        var result = await rig.Judge.JudgeAsync(A, B, Ct);

        var usage = Assert.IsType<JudgeUsage>(result.Usage);
        Assert.Equal(381, usage.InputTokens);
        Assert.Equal(20, usage.OutputTokens);
        Assert.Equal(0.000016002m, usage.CostUsd);
        Assert.Equal("typesafe/jev-1.13-20260917", usage.ResolvedModel);
        Assert.Equal("gen-dec-1", usage.RequestId);
    }

    [Theory]
    [InlineData("""{"model":"m"}""")]
    [InlineData("""{"answers":{"same":{"type":"noul"}}}""")]
    [InlineData("""{"answers":{"same":{"type":"noul","noul":"x"}}}""")]
    [InlineData("""{"answers":{"same":{"type":"noul","noul":1.5}}}""")]
    [InlineData("""{"answers":{"same":{"type":"noul","noul":-0.1}}}""")]
    public async Task MissingOrInvalidNoul_Throws(string json)
    {
        var rig = new Rig(_ => StubHttpHandler.Json(json));
        await Assert.ThrowsAsync<ModelResponseException>(() => rig.Judge.JudgeAsync(A, B, Ct));
    }

    [Fact]
    public async Task Http429_IsThrottled_WithRetryAfter_AndNotRetriedInPlace()
    {
        var rig = new Rig(_ => new HttpResponseMessage((HttpStatusCode)429)
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)) }
        });

        var ex = await Assert.ThrowsAsync<ModelThrottledException>(() => rig.Judge.JudgeAsync(A, B, Ct));

        Assert.Equal(TimeSpan.FromSeconds(30), ex.RetryAfter);
        Assert.Single(rig.Handler.Requests);
    }

    [Fact]
    public async Task Http502_IsUnavailable_And400_IsResponseError()
    {
        var down = new Rig(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        await Assert.ThrowsAsync<ModelUnavailableException>(() => down.Judge.JudgeAsync(A, B, Ct));

        var bad = new Rig(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        await Assert.ThrowsAsync<ModelResponseException>(() => bad.Judge.JudgeAsync(A, B, Ct));
    }

    [Fact]
    public void Construction_TakesNoGatewayThrottle() =>
        Assert.DoesNotContain(typeof(JevPairJudge).GetConstructors().Single().GetParameters(),
            p => p.ParameterType == typeof(GatewayThrottle));
}
