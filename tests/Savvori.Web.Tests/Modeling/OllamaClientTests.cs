using System.Net;
using Microsoft.Extensions.Options;
using Savvori.WebApi.Modeling;

namespace Savvori.Web.Tests.Modeling;

public sealed class OllamaClientTests
{
    private static readonly ModelOptions Opts = new() { EmbeddingModel = "bge-m3", JudgeModel = "qwen2.5:7b-instruct" };
    private const string Tags = """{"models":[{"name":"bge-m3:latest","digest":"abc123"}]}""";

    private static (OllamaEmbeddingClient Client, StubHttpHandler Handler) Embedder(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHttpHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://model.test/") };
        return (new OllamaEmbeddingClient(http, Options.Create(Opts), TimeProvider.System), handler);
    }

    private static HttpResponseMessage Route(HttpRequestMessage r, string embedJson) =>
        r.RequestUri!.AbsolutePath == "/api/tags"
            ? StubHttpHandler.Json(Tags)
            : StubHttpHandler.Json(embedJson);

    [Fact]
    public async Task Embed_ReturnsVectorsWithModelNameDigestAndDimension()
    {
        var (client, handler) = Embedder(r => Route(r, """{"embeddings":[[1,0,0],[0,1,0]]}"""));

        var result = await client.EmbedAsync(["a", "b"], TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Vectors.Count);
        Assert.Equal(3, result.Dimension);
        Assert.Equal("bge-m3", result.ModelName);
        Assert.Equal("abc123", result.ModelDigest);
        var embed = Assert.Single(handler.Requests, x => x.Path == "/api/embed");
        Assert.Contains("\"model\":\"bge-m3\"", embed.Body);
        Assert.Contains("\"input\":[\"a\",\"b\"]", embed.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Embed_ServerErrors_AreUnavailable(HttpStatusCode status)
    {
        var (client, _) = Embedder(_ => new HttpResponseMessage(status));
        await Assert.ThrowsAsync<ModelUnavailableException>(
            () => client.EmbedAsync(["a"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Embed_ClientError_IsResponseErrorNotUnavailable()
    {
        var (client, _) = Embedder(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        await Assert.ThrowsAsync<ModelResponseException>(
            () => client.EmbedAsync(["a"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Embed_ConnectionFailure_IsUnavailable()
    {
        var (client, _) = Embedder(_ => throw new HttpRequestException("connection refused"));
        await Assert.ThrowsAsync<ModelUnavailableException>(
            () => client.EmbedAsync(["a"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Embed_TimeoutNotRequestedByCaller_IsUnavailable()
    {
        var (client, _) = Embedder(_ => throw new TaskCanceledException("timed out"));
        await Assert.ThrowsAsync<ModelUnavailableException>(
            () => client.EmbedAsync(["a"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Embed_CallerCancellation_IsNotReportedAsUnavailable()
    {
        using var cts = new CancellationTokenSource();
        var (client, _) = Embedder(_ => { cts.Cancel(); throw new TaskCanceledException(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.EmbedAsync(["a"], cts.Token));
    }

    [Theory]
    [InlineData("""{"embeddings":[[1,0]]}""")]          // wrong count
    [InlineData("""{"embeddings":[[1,0],[1]]}""")]      // inconsistent dimensions
    [InlineData("""{"embeddings":[[],[]]}""")]          // empty vectors
    [InlineData("not json")]
    public async Task Embed_BadPayloads_AreResponseErrors(string body)
    {
        var (client, _) = Embedder(r => Route(r, body));
        await Assert.ThrowsAsync<ModelResponseException>(
            () => client.EmbedAsync(["a", "b"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Embed_ModelNotInstalled_IsUnavailable_BecauseNoDigestMeansNoTrust()
    {
        var (client, _) = Embedder(r => r.RequestUri!.AbsolutePath == "/api/tags"
            ? StubHttpHandler.Json("""{"models":[{"name":"other","digest":"zzz"}]}""")
            : StubHttpHandler.Json("""{"embeddings":[[1,0]]}"""));

        await Assert.ThrowsAsync<ModelUnavailableException>(
            () => client.EmbedAsync(["a"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Judge_SendsTemperatureZeroAndOnlyProductText_AndParsesAnswer()
    {
        var handler = new StubHttpHandler(_ =>
            StubHttpHandler.Json("""{"message":{"role":"assistant","content":"Yes."}}"""));
        var judge = new OllamaPairJudge(
            new HttpClient(handler) { BaseAddress = new Uri("http://model.test/") }, Options.Create(Opts));

        var verdict = await judge.JudgeAsync(
            new JudgeItem("Leite Meio Gordo", "Mimosa", "1 L", "continente"),
            new JudgeItem("Leite M. Gordo", "Mimosa", "1 L", "auchan"),
            TestContext.Current.CancellationToken);

        Assert.Equal(JudgeVerdict.Yes, verdict);
        var body = Assert.Single(handler.Requests).Body;
        Assert.Contains("/api/chat", handler.Requests[0].Path);
        Assert.Contains("\"temperature\":0", body);
        Assert.Contains("\"stream\":false", body);
        Assert.DoesNotContain("\"format\"", body); // no reliance on JSON mode
        Assert.Contains("qwen2.5:7b-instruct", body);
    }

    [Fact]
    public async Task Judge_TransportFailure_ThrowsInsteadOfAnswering_No()
    {
        var judge = new OllamaPairJudge(
            new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)))
            { BaseAddress = new Uri("http://model.test/") }, Options.Create(Opts));

        await Assert.ThrowsAsync<ModelUnavailableException>(() => judge.JudgeAsync(
            new JudgeItem("a", null, null, "x"), new JudgeItem("b", null, null, "y"),
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("yes", JudgeVerdict.Yes)]
    [InlineData("Yes.", JudgeVerdict.Yes)]
    [InlineData("  YES\n", JudgeVerdict.Yes)]
    [InlineData("**Yes**", JudgeVerdict.Yes)]
    [InlineData("\"yes\"", JudgeVerdict.Yes)]
    [InlineData("Yes, they are the same product", JudgeVerdict.Yes)]
    [InlineData("sim", JudgeVerdict.Yes)]
    [InlineData("no", JudgeVerdict.No)]
    [InlineData("No.", JudgeVerdict.No)]
    [InlineData("Não", JudgeVerdict.No)]
    [InlineData("unsure", JudgeVerdict.Unclear)]
    [InlineData("Not sure", JudgeVerdict.Unclear)]
    [InlineData("Maybe yes?", JudgeVerdict.Unclear)]
    [InlineData("I think they differ", JudgeVerdict.Unclear)]
    [InlineData("", JudgeVerdict.Unclear)]
    [InlineData(null, JudgeVerdict.Unclear)]
    [InlineData("...", JudgeVerdict.Unclear)]
    public void ParseVerdict_IsDefensive(string? content, JudgeVerdict expected) =>
        Assert.Equal(expected, OllamaPairJudge.ParseVerdict(content));
}
