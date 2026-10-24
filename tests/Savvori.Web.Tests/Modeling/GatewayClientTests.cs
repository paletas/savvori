using Savvori.Shared;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Savvori.WebApi.Modeling;

namespace Savvori.Web.Tests.Modeling;

public sealed class GatewayClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig
    {
        public ManualTimeProvider Time { get; } = new();
        public ModelOptions Opts { get; } = new();
        public GatewayThrottle Throttle { get; }
        public StubHttpHandler Handler { get; }
        public HttpClient Http { get; }

        public Rig(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            Throttle = new GatewayThrottle(Options.Create(Opts), Time, (_, _) => Task.CompletedTask);
            Handler = new StubHttpHandler(respond);
            Http = new HttpClient(Handler) { BaseAddress = new Uri("http://gateway.test/v1/") };
        }

        public GatewayEmbeddingClient Embedder() => new(Http, Options.Create(Opts), Throttle, Time);
        public GatewayCategoryJudge Category() => new(Http, Options.Create(Opts), Throttle, Time);
        public GatewayProductTranslator Translator() => new(Http, Options.Create(Opts), Throttle, Time);
    }

    private static string Vector(int dim) => "[" + string.Join(",", Enumerable.Repeat("0.5", dim)) + "]";

    private static string EmbedBody(int count, int dim) =>
        "{\"data\":[" + string.Join(",", Enumerable.Range(0, count).Select(_ => "{\"embedding\":" + Vector(dim) + "}")) + "]}";

    private static string Chat(string content) =>
        System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content } } } });

    private static HttpResponseMessage Throttled() =>
        new((HttpStatusCode)429)
        {
            Content = new StringContent("""{"error":{"message":"m","type":"rate_limit_error","code":"rate_limit_exceeded"}}""",
                System.Text.Encoding.UTF8, "application/json"),
            Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120)) }
        };

    private static readonly CategoryJudgeItem Item = new("Leite Meio Gordo", "Mimosa", null, "Leite");

    [Fact]
    public async Task Embed_Ok_ReturnsVectorsWithIdentity()
    {
        var rig = new Rig(_ => StubHttpHandler.Json(EmbedBody(2, 1024)));

        var result = await rig.Embedder().EmbedAsync(["a", "b"], Ct);

        Assert.Equal(2, result.Vectors.Count);
        Assert.Equal(1024, result.Dimension);
        Assert.Equal("bge-m3", result.ModelName);
        Assert.Equal("freellmapi:bge-m3", result.ModelDigest);
        var req = Assert.Single(rig.Handler.Requests);
        Assert.Equal("/v1/embeddings", req.Path);
        Assert.Contains("\"model\":\"bge-m3\"", req.Body);
        Assert.Contains("\"input\":[\"a\",\"b\"]", req.Body);
    }

    [Fact]
    public async Task Embed_CountMismatch_IsResponseError()
    {
        var rig = new Rig(_ => StubHttpHandler.Json(EmbedBody(1, 1024)));
        await Assert.ThrowsAsync<ModelResponseException>(() => rig.Embedder().EmbedAsync(["a", "b"], Ct));
    }

    [Fact]
    public async Task Embed_WrongDimension_IsResponseError()
    {
        var rig = new Rig(_ => StubHttpHandler.Json(EmbedBody(2, 3072)));
        await Assert.ThrowsAsync<ModelResponseException>(() => rig.Embedder().EmbedAsync(["a", "b"], Ct));
    }

    [Fact]
    public async Task Embed_EmptyInput_MakesNoCall()
    {
        var rig = new Rig(_ => StubHttpHandler.Json(EmbedBody(0, 1024)));
        var result = await rig.Embedder().EmbedAsync([], Ct);
        Assert.Empty(result.Vectors);
        Assert.Empty(rig.Handler.Requests);
    }

    [Fact]
    public async Task GetModelInfo_MakesNoRequest()
    {
        var rig = new Rig(_ => StubHttpHandler.Json("{}"));
        var info = await rig.Embedder().GetModelInfoAsync(Ct);
        Assert.Equal(new ModelInfo("bge-m3", "freellmapi:bge-m3"), info);
        Assert.Empty(rig.Handler.Requests);
    }

    [Fact]
    public async Task Ping_GetsModels_AndCountsInTheWindow()
    {
        var rig = new Rig(_ => StubHttpHandler.Json("""{"data":[]}"""));
        await rig.Embedder().PingAsync(Ct);
        var req = Assert.Single(rig.Handler.Requests);
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("/v1/models", req.Path);
        Assert.Equal(1, rig.Throttle.WindowCount);
    }

    [Fact]
    public async Task Embed_502_IsUnavailable()
    {
        var rig = new Rig(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        await Assert.ThrowsAsync<ModelUnavailableException>(() => rig.Embedder().EmbedAsync(["a"], Ct));
    }

    [Fact]
    public async Task Embed_RoutingRateLimit_IsThrottled()
    {
        var rig = new Rig(_ => Throttled());
        var ex = await Assert.ThrowsAsync<ModelThrottledException>(() => rig.Embedder().EmbedAsync(["a"], Ct));
        Assert.Equal(TimeSpan.FromSeconds(120), ex.RetryAfter);
        Assert.Equal(ThrottleKind.Routing, ex.Kind);
    }

    [Theory]
    [InlineData("Yes.", JudgeVerdict.Yes)]
    [InlineData("no", JudgeVerdict.No)]
    [InlineData("maybe", JudgeVerdict.Unclear)]
    public async Task Category_MapsAnswers_AndSendsExpectedRequest(string answer, JudgeVerdict expected)
    {
        var rig = new Rig(_ => StubHttpHandler.Json(Chat(answer)));

        var verdict = await rig.Category().JudgeAsync(Item, Ct);

        Assert.Equal(expected, verdict);
        var req = Assert.Single(rig.Handler.Requests);
        Assert.Equal("/v1/chat/completions", req.Path);
        Assert.Contains("\"max_tokens\":8", req.Body);
        Assert.Contains("\"temperature\":0", req.Body);
        Assert.Contains("mistral-small-3.1-24b", req.Body);
        Assert.DoesNotContain("response_format", req.Body);
    }

    [Fact]
    public async Task Category_429_IsThrottled_And502_IsUnavailable()
    {
        var throttled = new Rig(_ => Throttled());
        await Assert.ThrowsAsync<ModelThrottledException>(() => throttled.Category().JudgeAsync(Item, Ct));

        var down = new Rig(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        await Assert.ThrowsAsync<ModelUnavailableException>(() => down.Category().JudgeAsync(Item, Ct));
    }

    [Fact]
    public async Task Translator_SendsStrictSchema_AndParsesStrictAnswer()
    {
        const string answer = """
            {"products":[
              {"index":0,"pt":["arroz"],"en":["rice"],"es":["arroz"],"fr":["riz"]},
              {"index":1,"pt":["leite"],"en":["milk"],"es":["leche"],"fr":["lait"]}]}
            """;
        var rig = new Rig(_ => StubHttpHandler.Json(Chat(answer)));

        var results = await rig.Translator().TranslateAsync(
            [new TranslateItem("Arroz Agulha", "Cigala", null), new TranslateItem("Leite", null, "Laticinios")], Ct);

        Assert.Equal(2, results.Count);
        Assert.Equal("rice", results[0].Keywords["en"][0]);
        Assert.Equal("leche", results[1].Keywords["es"][0]);
        var body = Assert.Single(rig.Handler.Requests).Body;
        Assert.Contains("\"response_format\"", body);
        Assert.Contains("\"json_schema\"", body);
        Assert.Contains("\"strict\":true", body);
        Assert.Contains("\"name\":\"aliases\"", body);
        Assert.Contains("\"max_tokens\":2048", body);
    }

    [Fact]
    public async Task Translator_CountMismatch_IsResponseError()
    {
        var rig = new Rig(_ => StubHttpHandler.Json(Chat("""{"products":[]}""")));
        await Assert.ThrowsAsync<ModelResponseException>(
            () => rig.Translator().TranslateAsync([new TranslateItem("Arroz", null, null)], Ct));
    }

    [Fact]
    public async Task Translator_429_IsThrottled()
    {
        var rig = new Rig(_ => Throttled());
        await Assert.ThrowsAsync<ModelThrottledException>(
            () => rig.Translator().TranslateAsync([new TranslateItem("Arroz", null, null)], Ct));
    }

    [Fact]
    public async Task SharedThrottle_CountsEmbeddingAndCategoryCalls()
    {
        var rig = new Rig(r => r.RequestUri!.AbsolutePath.EndsWith("embeddings")
            ? StubHttpHandler.Json(EmbedBody(1, 1024))
            : StubHttpHandler.Json(Chat("yes")));

        await rig.Embedder().EmbedAsync(["a"], Ct);
        await rig.Category().JudgeAsync(Item, Ct);

        Assert.Equal(2, rig.Throttle.WindowCount);
    }
}
