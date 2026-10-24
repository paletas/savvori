using Savvori.Shared;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Savvori.WebApi.Modeling;

/// <summary>
/// Embeddings over the gateway's OpenAI-compatible <c>/v1/embeddings</c>. The gateway exposes no model digest, so the
/// identity is a fixed <c>freellmapi:&lt;model&gt;</c> string. The dimension must be 1024 (<c>auto</c> would return 3072-d vectors).
/// </summary>
public sealed class GatewayEmbeddingClient(
    HttpClient http, IOptions<ModelOptions> options, GatewayThrottle throttle, TimeProvider time) : IEmbeddingClient
{
    private const int ExpectedDimension = 1024;

    public async Task PingAsync(CancellationToken ct = default)
    {
        (await ModelHttp.SendAsync(http, () => new HttpRequestMessage(HttpMethod.Get, "models"), throttle, time, ct)).Dispose();
    }

    public Task<ModelInfo> GetModelInfoAsync(CancellationToken ct = default)
    {
        var model = options.Value.EmbeddingModel;
        return Task.FromResult(new ModelInfo(model, "freellmapi:" + model));
    }

    public async Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var model = options.Value.EmbeddingModel;
        var digest = "freellmapi:" + model;
        if (texts.Count == 0) return new([], model, digest, 0);

        var response = await ModelHttp.SendAsync(http, () => new HttpRequestMessage(HttpMethod.Post, "embeddings")
        {
            Content = JsonContent.Create(new { model, input = texts })
        }, throttle, time, ct);
        var body = await ModelHttp.ReadAsync<EmbedResponse>(response, ct);

        var vectors = body.Data?.Select(d => d.Embedding ?? []).ToList();
        if (vectors is null || vectors.Count != texts.Count)
            throw new ModelResponseException(
                $"Expected {texts.Count} embeddings, got {vectors?.Count ?? 0}.");
        var dim = vectors[0].Length;
        if (dim == 0 || vectors.Any(v => v.Length != dim))
            throw new ModelResponseException("Embeddings are empty or have inconsistent dimensions.");
        if (dim != ExpectedDimension)
            throw new ModelResponseException($"Expected {ExpectedDimension}-dimensional embeddings, got {dim}.");

        return new(vectors, model, digest, dim);
    }

    private sealed record EmbedResponse([property: JsonPropertyName("data")] List<Item>? Data);
    private sealed record Item([property: JsonPropertyName("embedding")] float[]? Embedding);
}

/// <summary>
/// Yes/no category-assignment judge over the gateway <c>chat/completions</c> (temperature 0, no JSON mode). Tuned by hand
/// against real prod category decisions (2026-09-23): the few-shot examples below are load-bearing — removing
/// or narrowing them measurably regressed accuracy in that experiment (71.7% -> 83.3% -> 88.5% held-out, then
/// baby-gear/wipes/books examples added after a beta bulk-apply run showed those over-rejected; see
/// docs/MODEL_MATCHING_PLAN.md). Don't trim them without re-measuring.
/// </summary>
public sealed class GatewayCategoryJudge(
    HttpClient http, IOptions<ModelOptions> options, GatewayThrottle throttle, TimeProvider time) : ICategoryJudge
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
        var response = await ModelHttp.SendAsync(http, () => new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model = options.Value.CategoryModel,
                max_tokens = 8,
                temperature = 0,
                messages = new object[]
                {
                    new { role = "system", content = SystemPrompt },
                    new { role = "user", content = $"Product: {Describe(item)}\nAnswer:" }
                }
            })
        }, throttle, time, ct);
        var body = await ModelHttp.ReadAsync<ChatResponse>(response, ct);
        return VerdictParser.Parse(body.Choices?.FirstOrDefault()?.Message?.Content);
    }

    private static string Describe(CategoryJudgeItem i) =>
        $"{i.Name} | Brand: {i.Brand ?? "(none)"} | Store category: {i.StoreCategory ?? "(none)"} | Proposed: {i.SuggestedCategory}";

    private sealed record ChatResponse([property: JsonPropertyName("choices")] List<Choice>? Choices);
    private sealed record Choice([property: JsonPropertyName("message")] ChatMessage? Message);
    private sealed record ChatMessage([property: JsonPropertyName("content")] string? Content);
}

/// <summary>
/// Generic product names and search keywords per language over the gateway <c>chat/completions</c> with a strict
/// JSON-schema <c>response_format</c> (temperature 0). Several products go in one request; answers are matched back by index.
/// </summary>
public sealed class GatewayProductTranslator(
    HttpClient http, IOptions<ModelOptions> options, GatewayThrottle throttle, TimeProvider time) : IProductTranslator
{
    public async Task<IReadOnlyList<TranslateResult>> TranslateAsync(
        IReadOnlyList<TranslateItem> items, CancellationToken ct = default)
    {
        if (items.Count == 0) return [];
        var prompt = string.Join("\n", items.Select((it, i) =>
            $"{i}: {it.Name} | Brand: {it.Brand ?? "(none)"} | Store category: {it.Category ?? "(none)"}"));

        var response = await ModelHttp.SendAsync(http, () => new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model = options.Value.TranslatorModel,
                max_tokens = 2048,
                temperature = 0,
                response_format = new
                {
                    type = "json_schema",
                    json_schema = new { name = "aliases", strict = true, schema = ProductTranslation.Schema }
                },
                messages = new object[]
                {
                    new { role = "system", content = ProductTranslation.SystemPrompt },
                    new { role = "user", content = prompt }
                }
            })
        }, throttle, time, ct);
        var body = await ModelHttp.ReadAsync<ChatResponse>(response, ct);
        return ProductTranslation.Parse(body.Choices?.FirstOrDefault()?.Message?.Content, items.Count);
    }

    private sealed record ChatResponse([property: JsonPropertyName("choices")] List<Choice>? Choices);
    private sealed record Choice([property: JsonPropertyName("message")] ChatMessage? Message);
    private sealed record ChatMessage([property: JsonPropertyName("content")] string? Content);
}
