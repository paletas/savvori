using Savvori.Shared;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Savvori.WebApi.Modeling;

internal static class OllamaHttp
{
    /// <summary>
    /// Sends a request and maps every transport-level failure (connect error, timeout, 5xx, 404 = model missing)
    /// to <see cref="ModelUnavailableException"/>. Cancellation by the caller is rethrown untouched.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            var response = await http.SendAsync(request, ct);
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.NotFound)
            {
                var status = response.StatusCode;
                response.Dispose();
                throw new ModelUnavailableException($"Model server returned {(int)status} {status}.");
            }
            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode;
                response.Dispose();
                throw new ModelResponseException($"Model server rejected the request with {(int)status} {status}.");
            }
            return response;
        }
        catch (HttpRequestException ex)
        {
            throw new ModelUnavailableException($"Model server unreachable: {ex.Message}", ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ModelUnavailableException("Model request timed out.", ex);
        }
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<T>(ct);
            return body ?? throw new ModelResponseException("Model server returned an empty body.");
        }
        catch (JsonException ex)
        {
            throw new ModelResponseException("Model server returned malformed JSON.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ModelUnavailableException($"Model response was cut off: {ex.Message}", ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ModelUnavailableException("Model response timed out.", ex);
        }
        finally
        {
            response.Dispose();
        }
    }
}

// placeholder until Batch 3 (JevPairJudge); Batch 6 deletes this file
/// <summary>Yes/no product-pair judge over Ollama <c>/api/chat</c> (temperature 0, no JSON mode).</summary>
public sealed class OllamaPairJudge(HttpClient http, IOptions<ModelOptions> options) : IPairJudge
{
    private const string SystemPrompt =
        "You compare two supermarket listings from different stores. Answer with exactly one word: " +
        "yes if they are the same product (same brand, variety/flavour and pack size), no if they differ, " +
        "unsure if you cannot tell.";

    public async Task<JudgeVerdict> JudgeAsync(JudgeItem a, JudgeItem b, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat")
        {
            Content = JsonContent.Create(new
            {
                model = options.Value.PairJudgeModel,
                stream = false,
                options = new { temperature = 0, num_predict = 8 },
                messages = new object[]
                {
                    new { role = "system", content = SystemPrompt },
                    new { role = "user", content = $"A: {Describe(a)}\nB: {Describe(b)}\nSame product?" }
                }
            })
        };
        var response = await OllamaHttp.SendAsync(http, request, ct);
        var body = await OllamaHttp.ReadAsync<ChatResponse>(response, ct);
        return ParseVerdict(body.Message?.Content);
    }

    private static string Describe(JudgeItem i) =>
        string.Join(" | ", new[] { i.Name, i.Brand, i.Size, i.Chain }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public static JudgeVerdict ParseVerdict(string? content) => VerdictParser.Parse(content);

    private sealed record ChatResponse([property: JsonPropertyName("message")] ChatMessage? Message);
    private sealed record ChatMessage([property: JsonPropertyName("content")] string? Content);
}
