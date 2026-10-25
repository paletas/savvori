using System.Text.Json;
using System.Text.Json.Serialization;

namespace Savvori.WebApi.Modeling;

/// <summary>
/// Shared pieces of the product translator: the languages, the prompt, the JSON schema and the strict answer parse.
/// Several products go in one request; answers are matched back by index.
/// </summary>
public static class ProductTranslation
{
    public static readonly string[] Languages = ["pt", "en", "es", "fr"];

    internal const string SystemPrompt =
        """
        You help a Portuguese supermarket price-comparison site find products across languages.
        For each numbered product, give what the product IS as 1 to 3 short generic search keywords a shopper might
        type, in Portuguese (pt), English (en), Spanish (es) and French (fr). Most generic name first.

        Rules:
        - Describe the product itself ("arroz agulha" -> en: "rice", "long grain rice"). Each list is in its own
          language only: no Portuguese words in the English list, no English words in the Portuguese one.
        - Never include brand names, flavours, ingredients, sizes or marketing words. A shampoo with beer in it is a
          shampoo, peanuts in milk chocolate are peanuts or chocolate snacks, not milk, and a rice cake is a cake.
        - Use the store category, when given, to tell what the product is. A word that only shares a name with a food
          (sun lotion called "leite solar") is not that food.
        - Keywords are nouns a shopper would search, 1 to 3 words each, not sentences.
        - If you are not sure what the product is, return empty lists for it rather than guessing.
        - Answer with JSON only, one entry per product, using the given index.
        """;

    internal static readonly object Schema = BuildSchema();

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
}
