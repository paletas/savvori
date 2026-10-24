using Savvori.Shared;
using System.Net;
using Microsoft.Extensions.Options;
using Savvori.WebApi.Modeling;

namespace Savvori.Web.Tests.Modeling;

public sealed class OllamaClientTests
{
    private static readonly ModelOptions Opts = new() { PairJudgeModel = "qwen2.5:7b-instruct" };

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
