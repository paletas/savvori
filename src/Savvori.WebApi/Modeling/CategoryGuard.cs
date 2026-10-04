using System.Text.RegularExpressions;
using Savvori.WebApi.Scraping;

namespace Savvori.WebApi.Modeling;

/// <summary>
/// Deterministic check for the category classifier's most common mistake: a product name that shares a literal
/// word with a food category ("leite", "queijo", "arroz", ...) without actually being that kind of food — an
/// appliance, a tool, a cosmetic, a book, a toy, a medicine, or another chain's pet food. The embedding matches on
/// the word, not on what the product is.
///
/// Tuned by hand on a real prod sample of suggestions at 100% confidence that were still wrong (see
/// docs/MODEL_MATCHING_PLAN.md); it errs towards flagging, which only costs a manual review, never a wrong assignment.
/// </summary>
public static class CategoryGuard
{
    // A product carrying one of these words is not naming an ingredient, whatever category it best matches by text.
    private static readonly HashSet<string> NonFoodObjects = new(
        ("maquina cortador colher tabua dispensador escova intercomunicador solucao banco mesa bau almofada " +
         "espelho aquecedor moedor abridor raladeira polir champo").Split(' '));

    // Categories where a non-food object word is expected and should not be flagged (kitchen tools, baby gear, ...).
    // Furniture and cushions are deliberately NOT here: "Limpeza do Lar" is cleaning products, not furniture, and a
    // real miss (children's furniture) was suggested there by the classifier.
    private static readonly HashSet<string> ObjectCategories = new(
        ["puericultura e mobiliario bebe", "cosmetica, rosto e corpo", "protecao solar",
         "fraldas e higiene bebe", "papelaria e livros", "cabelo"]);

    // A product naming another chain's pet food is never a human-food category.
    private static readonly HashSet<string> PetWords = new(["cao", "caes", "gato", "gatos", "racao"]);
    private static readonly HashSet<string> PetCategories = new(["comida para caes", "comida para gatos"]);

    private static readonly Regex Word = new(@"[a-z0-9]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>True if the suggestion looks like a literal-word collision rather than a real match.</summary>
    public static bool Suspicious(string productName, string categoryName)
    {
        var text = ProductNormalizer.Normalize(productName);
        var words = Word.Matches(text).Select(m => m.Value).ToHashSet();
        var category = ProductNormalizer.Normalize(categoryName);

        if (words.Overlaps(PetWords) && !PetCategories.Contains(category)) return true;
        if (words.Overlaps(NonFoodObjects) && !ObjectCategories.Contains(category)) return true;
        // "Congelado" (frozen) and "Gelado" (ice cream) share almost the same letters, and embeddings confuse
        // them: frozen fish/octopus/vegetables kept landing under Gelados. A frozen product is essentially never
        // really ice cream, so this one word pair gets a dedicated check instead of a general word list.
        if (text.Contains("congelad") && category == "gelados") return true;
        return false;
    }
}
