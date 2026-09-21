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

/// <summary>Ollama embeddings via <c>/api/embed</c>; the model digest comes from <c>/api/tags</c> (cached briefly).</summary>
public sealed class OllamaEmbeddingClient(HttpClient http, IOptions<ModelOptions> options, TimeProvider time)
    : IEmbeddingClient
{
    private static readonly TimeSpan DigestTtl = TimeSpan.FromMinutes(10);
    private string? _digest;
    private DateTimeOffset _digestFetchedAt;

    public async Task PingAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/tags");
        (await OllamaHttp.SendAsync(http, request, ct)).Dispose();
    }

    public async Task<ModelInfo> GetModelInfoAsync(CancellationToken ct = default)
    {
        var model = options.Value.EmbeddingModel;
        return new ModelInfo(model, await GetDigestAsync(model, ct));
    }

    public async Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var model = options.Value.EmbeddingModel;
        if (texts.Count == 0) return new([], model, await GetDigestAsync(model, ct), 0);

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/embed")
        {
            Content = JsonContent.Create(new { model, input = texts })
        };
        var response = await OllamaHttp.SendAsync(http, request, ct);
        var body = await OllamaHttp.ReadAsync<EmbedResponse>(response, ct);

        if (body.Embeddings is null || body.Embeddings.Count != texts.Count)
            throw new ModelResponseException(
                $"Expected {texts.Count} embeddings, got {body.Embeddings?.Count ?? 0}.");
        var dim = body.Embeddings[0].Length;
        if (dim == 0 || body.Embeddings.Any(v => v.Length != dim))
            throw new ModelResponseException("Embeddings are empty or have inconsistent dimensions.");

        return new(body.Embeddings, model, await GetDigestAsync(model, ct), dim);
    }

    private async Task<string> GetDigestAsync(string model, CancellationToken ct)
    {
        if (_digest is not null && time.GetUtcNow() - _digestFetchedAt < DigestTtl) return _digest;

        using var request = new HttpRequestMessage(HttpMethod.Get, "api/tags");
        var tags = await OllamaHttp.ReadAsync<TagsResponse>(await OllamaHttp.SendAsync(http, request, ct), ct);
        var entry = tags.Models?.FirstOrDefault(m =>
            string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(m.Name, model + ":latest", StringComparison.OrdinalIgnoreCase));
        // Without a digest we cannot prove two vectors come from the same model, so treat it as unavailable.
        if (string.IsNullOrEmpty(entry?.Digest))
            throw new ModelUnavailableException($"Model '{model}' is not installed on the server.");

        _digest = entry.Digest;
        _digestFetchedAt = time.GetUtcNow();
        return _digest;
    }

    private sealed record EmbedResponse([property: JsonPropertyName("embeddings")] List<float[]>? Embeddings);
    private sealed record TagsResponse([property: JsonPropertyName("models")] List<TagModel>? Models);
    private sealed record TagModel(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("digest")] string? Digest);
}

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
                model = options.Value.JudgeModel,
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

    /// <summary>
    /// Defensive one-word parse: first word only, ignoring case, punctuation and quotes.
    /// Anything that is not clearly yes/no (including empty, "not sure", chatter) is Unclear.
    /// </summary>
    public static JudgeVerdict ParseVerdict(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return JudgeVerdict.Unclear;
        var trimmed = content.TrimStart(' ', '\t', '\r', '\n', '"', '\'', '*', '`');
        var word = new string(trimmed.TakeWhile(char.IsLetter).ToArray()).ToLowerInvariant();
        return word switch
        {
            "yes" or "sim" => JudgeVerdict.Yes,
            "no" or "não" or "nao" => JudgeVerdict.No,
            _ => JudgeVerdict.Unclear
        };
    }

    private sealed record ChatResponse([property: JsonPropertyName("message")] ChatMessage? Message);
    private sealed record ChatMessage([property: JsonPropertyName("content")] string? Content);
}
