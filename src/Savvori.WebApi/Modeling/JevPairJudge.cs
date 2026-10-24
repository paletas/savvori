using Savvori.Shared;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Savvori.WebApi.Modeling;

/// <summary>
/// The pair judge: Jev (a decision model) on OpenRouter, <c>POST api/alpha/decisions</c>. It answers one "noul" question
/// with a probability in 0..1; at or above <c>Matching:PairJudgeThreshold</c> counts as a yes. Jev never says "unclear".
/// OpenRouter has no limiter, so there is no gateway throttle: a 429 surfaces as <see cref="ModelThrottledException"/>.
/// </summary>
public sealed class JevPairJudge(
    HttpClient http, IOptions<ModelOptions> options, TimeProvider time, IModelCallRecorder? recorder = null) : IPairJudge
{
    private readonly IModelCallRecorder _recorder = recorder ?? NullModelCallRecorder.Instance;

    internal const string Instructions = "Are listing A and listing B the same product (same brand, variety/flavour and pack size)?";
    internal const string TrueCriterion = "Same brand, same variety or flavour and the same pack size; only the store or the wording differs.";
    internal const string FalseCriterion = "A different brand, variety, flavour or pack size.";

    public async Task<PairJudgement> JudgeAsync(JudgeItem a, JudgeItem b, CancellationToken ct = default)
    {
        var o = options.Value;
        var body = new
        {
            model = o.PairJudgeModel,
            state = BuildState(a, b),
            questions = new
            {
                same = new
                {
                    type = "noul",
                    instructions = Instructions,
                    criteria = new Dictionary<string, string> { ["true"] = TrueCriterion, ["false"] = FalseCriterion }
                }
            }
        };
        var call = new ModelCallContext(ModelBackend.OpenRouter, "pair", o.PairJudgeModel, 1);
        var (response, elapsed) = await ModelHttp.SendTimedAsync(http, () => new HttpRequestMessage(HttpMethod.Post, "api/alpha/decisions")
        {
            Content = JsonContent.Create(body)
        }, null, time, ct, call, _recorder);
        var parsed = await ModelHttp.ReadAsync<DecisionResponse>(response, call, _recorder, elapsed, ToUsage, ct);
        return Map(parsed, o.Matching.PairJudgeThreshold);
    }

    public static string Describe(JudgeItem i) =>
        string.Join(" | ", new[] { i.Name, i.Brand, i.Size, i.Chain }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public static string BuildState(JudgeItem a, JudgeItem b) => $"A: {Describe(a)}\nB: {Describe(b)}";

    internal static PairJudgement Map(DecisionResponse r, double threshold)
    {
        double? noul = null;
        if (r.Answers is not null && r.Answers.TryGetValue("same", out var same)) noul = same?.Noul;
        if (noul is not { } score || double.IsNaN(score) || score < 0 || score > 1)
            throw new ModelResponseException("Jev returned no usable 'same' score.");
        return new PairJudgement(score >= threshold ? JudgeVerdict.Yes : JudgeVerdict.No, score, ToUsage(r));
    }

    private static JudgeUsage ToUsage(DecisionResponse r) =>
        new(r.Usage?.InputTokens, r.Usage?.OutputTokens, r.Usage?.Cost, r.Model, r.Id);

    internal sealed record DecisionResponse(
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("answers")] Dictionary<string, Answer?>? Answers,
        [property: JsonPropertyName("usage")] Usage? Usage,
        [property: JsonPropertyName("id")] string? Id);
    internal sealed record Answer(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("noul")] double? Noul);
    internal sealed record Usage(
        [property: JsonPropertyName("input_tokens")] int? InputTokens,
        [property: JsonPropertyName("output_tokens")] int? OutputTokens,
        [property: JsonPropertyName("cost")] decimal? Cost);
}
