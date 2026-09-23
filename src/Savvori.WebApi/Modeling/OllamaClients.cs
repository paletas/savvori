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

/// <summary>
/// Yes/no category-assignment judge over Ollama <c>/api/chat</c> (temperature 0, no JSON mode). Tuned by hand
/// against real prod category decisions (2026-09-23): the few-shot examples below are load-bearing — removing
/// or narrowing them measurably regressed accuracy in that experiment (71.7% -> 83.3% -> 88.5% held-out, then
/// baby-gear/wipes/books examples added after a beta bulk-apply run showed those over-rejected; see
/// docs/MODEL_MATCHING_PLAN.md). Don't trim them without re-measuring.
/// </summary>
public sealed class OllamaCategoryJudge(HttpClient http, IOptions<ModelOptions> options) : ICategoryJudge
{
    private const string SystemPrompt =
        """
        You are checking a product-category assignment for a Portuguese grocery price-comparison catalog.

        Judge whether the product genuinely IS the kind of thing the category names - not whether its name merely
        shares a word with the category. A product that is really a variant, flavor, format, or dietary version of
        the category (sem lactose, light, UHT, biologico, fundido, para barrar, infantil, sem gluten, congelado
        version of a frozen-food category, etc.) still belongs in that category: say yes. Say no only when the
        product is not actually that kind of food/thing at all - an appliance, a tool, a cosmetic, a toy, a book,
        a medicine, another chain's pet food, or a different food entirely that just shares a word in its name
        (e.g. "leite" meaning sunscreen lotion, "queijo" meaning a cushion shaped like a cheese, "congelado"
        meaning frozen but suggested into "Gelados"/ice cream). Baby and children's gear (cots, beds, playpens,
        wipes, bottles) and children's books are genuinely their own category even when the name sounds playful,
        uses a toy brand, or is unfamiliar - don't say no just because a product reads differently from a plain
        adult one.

        Examples:
        Product: Leite Meio Gordo Bio Prado Verde | Brand: Prado Verde | Store category: (none) | Proposed: Leite
        Answer: yes

        Product: MASSAS HELICES MILANEZA TRICOLORES ESPECIAL SALADA 500G | Brand: Milaneza | Store category: (none) | Proposed: Massas
        Answer: yes

        Product: RAÇÃO PARA GATINHOS PRO PLAN COM FRANGO E ARROZ 400G | Brand: Pro Plan | Store category: (none) | Proposed: Comida para Gatos
        Answer: yes

        Product: Gelado Cornetto Mini Clássico | Brand: Cornetto | Store category: (none) | Proposed: Gelados
        Answer: yes

        Product: MÁQUINA DE COZER ARROZ QILIVE Q.5130 3.7 L 700 W COM CESTO VAPORIZADOR | Brand: QILIVE | Store category: (none) | Proposed: Arroz
        Answer: no

        Product: SOLUÇÃO MAGNESIA PHILIPS LEITE 83MG/ML 200ML | Brand: Philips | Store category: (none) | Proposed: Leite
        Answer: no

        Product: Yarrah Cao Pate Frango Algas Bio 150G | Brand: Yarrah | Store category: (none) | Proposed: Queijos
        Answer: no

        Product: Tentáculos de Polvo Congelados Continente | Brand: Continente | Store category: Congelado | Proposed: Gelados
        Answer: no

        Product: Miolo de Camarão Selvagem 30/50 Congelado Continente | Brand: Continente | Store category: Congelado | Proposed: Marisco
        Answer: yes

        Product: LEITE SOLAR MUSTELA ROSTO SPF50+ 40ML | Brand: Mustela | Store category: (none) | Proposed: Proteção Solar
        Answer: yes

        Product: Puré De Maçã, Banana E Alperce Biológico 6M | Brand: Holle | Store category: (none) | Proposed: Frutas
        Answer: yes

        Product: Noilly Vermute Prat Dry | Brand: Noilly | Store category: Aperitivos | Proposed: Vinho
        Answer: yes

        Product: Cama Júnior com Proteção e Gavetão Branco Timo Twinko | Brand: Twinko | Store category: Camas, Berços e Colchões | Proposed: Puericultura e Mobiliário Bebé
        Answer: yes

        Product: Lupilu Toalhitas para Bebé Comfort | Brand: Lupilu | Store category: (none) | Proposed: Fraldas e Higiene Bebé
        Answer: yes

        Product: Disney Baby - As Palavras Mágicas | Brand: (none) | Store category: Livros para Bebé | Proposed: Papelaria e Livros
        Answer: yes

        Product: O Coelho Que Queria Dormir de Carl-Johan Forssen Ehrlin | Brand: Carl-Johan Forssen Ehrlin | Store category: Gravidez e Puericultura | Proposed: Papelaria e Livros
        Answer: yes

        Product: Zoko Happy Bear - Ouriço Dorme com as Estrelas | Brand: Zoko Happy Bear | Store category: Brinquedos de Bebé | Proposed: Puericultura e Mobiliário Bebé
        Answer: yes

        Answer with exactly one word: yes or no.
        """;

    public async Task<JudgeVerdict> JudgeAsync(CategoryJudgeItem item, CancellationToken ct = default)
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
                    new { role = "user", content = $"Product: {Describe(item)}\nAnswer:" }
                }
            })
        };
        var response = await OllamaHttp.SendAsync(http, request, ct);
        var body = await OllamaHttp.ReadAsync<ChatResponse>(response, ct);
        return OllamaPairJudge.ParseVerdict(body.Message?.Content);
    }

    private static string Describe(CategoryJudgeItem i) =>
        $"{i.Name} | Brand: {i.Brand ?? "(none)"} | Store category: {i.StoreCategory ?? "(none)"} | Proposed: {i.SuggestedCategory}";

    private sealed record ChatResponse([property: JsonPropertyName("message")] ChatMessage? Message);
    private sealed record ChatMessage([property: JsonPropertyName("content")] string? Content);
}

/// <summary>
/// Generic product names and search keywords per language over Ollama <c>/api/chat</c> with a JSON-schema
/// <c>format</c> (temperature 0). Several products go in one request; answers are matched back by index.
/// </summary>
public sealed class OllamaProductTranslator(HttpClient http, IOptions<ModelOptions> options) : IProductTranslator
{
    public static readonly string[] Languages = ["pt", "en", "es", "fr"];

    private const string SystemPrompt =
        """
        You help a Portuguese supermarket price-comparison site find products across languages.
        For each numbered product, give the generic name and up to 4 short search keywords a shopper might type,
        in Portuguese (pt), English (en), Spanish (es) and French (fr). Put the most generic name first.

        Rules:
        - Describe what the product IS ("arroz agulha" -> en: "rice", "long grain rice"). Never translate brand names.
        - Use the store category, when given, to tell what the product is. A word that only shares a name with a food
          (sun lotion called "leite solar") is not that food.
        - Keywords are nouns a shopper would search, not sentences. No sizes, no marketing words.
        - If you are not sure what the product is, return empty lists for it rather than guessing.
        - Answer with JSON only, one entry per product, using the given index.
        """;

    private static readonly object Schema = BuildSchema();

    private static object BuildSchema()
    {
        var list = new { type = "array", items = new { type = "string" } };
        return new
        {
            type = "object",
            properties = new
            {
                products = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        properties = new { index = new { type = "integer" }, pt = list, en = list, es = list, fr = list },
                        required = new[] { "index", "pt", "en", "es", "fr" }
                    }
                }
            },
            required = new[] { "products" }
        };
    }

    public async Task<IReadOnlyList<TranslateResult>> TranslateAsync(
        IReadOnlyList<TranslateItem> items, CancellationToken ct = default)
    {
        if (items.Count == 0) return [];
        var prompt = string.Join("\n", items.Select((it, i) =>
            $"{i}: {it.Name} | Brand: {it.Brand ?? "(none)"} | Store category: {it.Category ?? "(none)"}"));

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat")
        {
            Content = JsonContent.Create(new
            {
                model = options.Value.JudgeModel,
                stream = false,
                format = Schema,
                options = new { temperature = 0 },
                messages = new object[]
                {
                    new { role = "system", content = SystemPrompt },
                    new { role = "user", content = prompt }
                }
            })
        };
        var response = await OllamaHttp.SendAsync(http, request, ct);
        var body = await OllamaHttp.ReadAsync<ChatResponse>(response, ct);
        return Parse(body.Message?.Content, items.Count);
    }

    /// <summary>Strict parse: the answer must contain exactly one entry per input index, otherwise it is unusable.</summary>
    public static IReadOnlyList<TranslateResult> Parse(string? content, int count)
    {
        if (string.IsNullOrWhiteSpace(content)) throw new ModelResponseException("Translator returned no content.");
        Answer? answer;
        try { answer = JsonSerializer.Deserialize<Answer>(content); }
        catch (JsonException ex) { throw new ModelResponseException("Translator returned malformed JSON.", ex); }
        if (answer?.Products is null || answer.Products.Count != count)
            throw new ModelResponseException($"Expected {count} products, got {answer?.Products?.Count ?? 0}.");

        var results = new TranslateResult?[count];
        foreach (var p in answer.Products)
        {
            if (p.Index is not { } i || i < 0 || i >= count || results[i] is not null)
                throw new ModelResponseException("Translator returned a missing or duplicate index.");
            results[i] = new TranslateResult(new Dictionary<string, IReadOnlyList<string>>
            {
                ["pt"] = p.Pt ?? [], ["en"] = p.En ?? [], ["es"] = p.Es ?? [], ["fr"] = p.Fr ?? []
            });
        }
        return results!;
    }

    private sealed record Answer([property: JsonPropertyName("products")] List<Entry>? Products);
    private sealed record Entry(
        [property: JsonPropertyName("index")] int? Index,
        [property: JsonPropertyName("pt")] List<string>? Pt,
        [property: JsonPropertyName("en")] List<string>? En,
        [property: JsonPropertyName("es")] List<string>? Es,
        [property: JsonPropertyName("fr")] List<string>? Fr);
    private sealed record ChatResponse([property: JsonPropertyName("message")] ChatMessage? Message);
    private sealed record ChatMessage([property: JsonPropertyName("content")] string? Content);
}
