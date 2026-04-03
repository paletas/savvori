namespace Savvori.WebApi.Scraping;

/// <summary>
/// Maps scraped category strings from Portuguese grocery stores to canonical category slugs.
/// </summary>
public static class CategoryMapper
{
    // Exact-match and keyword lookup: scraped token → canonical slug
    private static readonly Dictionary<string, string> _map =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // ── Leite ──────────────────────────────────────────────────────────
            ["leite-uht"]           = "leite",
            ["leite-meio-gordo"]    = "leite",
            ["leite-magro"]         = "leite",
            ["leite-gordo"]         = "leite",
            ["leite"]               = "leite",

            // ── Iogurtes ──────────────────────────────────────────────────────
            ["iogurte"]             = "iogurtes",
            ["iogurtes"]            = "iogurtes",
            ["iogurtes-solidos"]    = "iogurtes",
            ["iogurtes-liquidos"]   = "iogurtes",

            // ── Queijos ────────────────────────────────────────────────────────
            ["queijo"]              = "queijos",
            ["queijos"]             = "queijos",

            // ── Manteiga e Margarinas ──────────────────────────────────────────
            ["manteiga"]            = "manteiga-margarinas",
            ["margarina"]           = "manteiga-margarinas",
            ["margarinas"]          = "manteiga-margarinas",

            // ── Natas e Cremes ─────────────────────────────────────────────────
            ["natas"]               = "natas-cremes",
            ["creme"]               = "natas-cremes",
            ["cremes"]              = "natas-cremes",

            // ── Laticínios (parent fallback) ───────────────────────────────────
            ["produtos-lacteos"]    = "laticinios",
            ["laticinios"]          = "laticinios",

            // ── Frutas ─────────────────────────────────────────────────────────
            ["fruta"]               = "frutas",
            ["frutas"]              = "frutas",
            ["frutos"]              = "frutas",

            // ── Legumes ────────────────────────────────────────────────────────
            ["legumes"]             = "legumes",
            ["horticolas"]          = "legumes",
            ["vegetais"]            = "legumes",
            ["verduras"]            = "legumes",

            // ── Carne ──────────────────────────────────────────────────────────
            ["carne"]               = "carne",
            ["frango"]              = "carne",
            ["porco"]               = "carne",
            ["vaca"]                = "carne",
            ["borrego"]             = "carne",

            // ── Peixe e Marisco ────────────────────────────────────────────────
            ["peixe"]               = "peixe-marisco",
            ["marisco"]             = "peixe-marisco",
            ["bacalhau"]            = "peixe-marisco",
            ["atum"]                = "peixe-marisco",

            // ── Ovos ───────────────────────────────────────────────────────────
            ["ovos"]                = "ovos",

            // ── Charcutaria ────────────────────────────────────────────────────
            ["charcutaria"]         = "charcutaria",
            ["fiambre"]             = "charcutaria",
            ["salsicha"]            = "charcutaria",
            ["chourico"]            = "charcutaria",

            // ── Frescos (parent fallback) ──────────────────────────────────────
            ["frescos"]             = "frescos",

            // ── Arroz ──────────────────────────────────────────────────────────
            ["arroz"]               = "arroz",

            // ── Massas ─────────────────────────────────────────────────────────
            ["massa"]               = "massas",
            ["massas"]              = "massas",
            ["esparguete"]          = "massas",
            ["fusilli"]             = "massas",

            // ── Conservas ──────────────────────────────────────────────────────
            ["conserva"]            = "conservas",
            ["conservas"]           = "conservas",
            ["atum-em-lata"]        = "conservas",

            // ── Molhos e Temperos ──────────────────────────────────────────────
            ["molho"]               = "molhos-temperos",
            ["molhos"]              = "molhos-temperos",
            ["ketchup"]             = "molhos-temperos",
            ["maionese"]            = "molhos-temperos",
            ["mostarda"]            = "molhos-temperos",
            ["tempero"]             = "molhos-temperos",

            // ── Azeite e Óleos ─────────────────────────────────────────────────
            ["azeite"]              = "azeite-oleos",
            ["oleo"]                = "azeite-oleos",
            ["oleos"]               = "azeite-oleos",

            // ── Cereais ────────────────────────────────────────────────────────
            ["cereal"]              = "cereais",
            ["cereais"]             = "cereais",
            ["granola"]             = "cereais",
            ["musli"]               = "cereais",

            // ── Bolachas ───────────────────────────────────────────────────────
            ["bolacha"]             = "bolachas",
            ["bolachas"]            = "bolachas",
            ["biscoito"]            = "bolachas",
            ["biscoitos"]           = "bolachas",

            // ── Mercearia (parent fallback) ────────────────────────────────────
            ["mercearia"]           = "mercearia",

            // ── Pão ────────────────────────────────────────────────────────────
            ["pao"]                 = "pao",
            ["pão"]                 = "pao",
            ["padaria"]             = "pao",
            ["baguete"]             = "pao",

            // ── Bolos e Sobremesas ─────────────────────────────────────────────
            ["bolo"]                = "bolos-sobremesas",
            ["bolos"]               = "bolos-sobremesas",
            ["sobremesa"]           = "bolos-sobremesas",
            ["sobremesas"]          = "bolos-sobremesas",
            ["pastelaria"]          = "bolos-sobremesas",

            // ── Água ───────────────────────────────────────────────────────────
            ["agua"]                = "agua",
            ["água"]                = "agua",

            // ── Sumos ──────────────────────────────────────────────────────────
            ["sumo"]                = "sumos",
            ["sumos"]               = "sumos",
            ["nectar"]              = "sumos",
            ["nectares"]            = "sumos",
            ["bebida-vegetal"]      = "sumos",

            // ── Bebidas Alcoólicas ─────────────────────────────────────────────
            ["cerveja"]             = "bebidas-alcoolicas",
            ["vinho"]               = "bebidas-alcoolicas",
            ["bebida-alcoolica"]    = "bebidas-alcoolicas",
            ["alcool"]              = "bebidas-alcoolicas",

            // ── Bebidas (parent fallback) ──────────────────────────────────────
            ["bebidas"]             = "bebidas",

            // ── Congelados ─────────────────────────────────────────────────────
            ["congelados"]          = "congelados",
            ["congelado"]           = "congelados",
            ["legumes-congelados"]  = "legumes-congelados",
            ["peixe-congelado"]     = "peixe-congelado",
            ["refeicao"]            = "refeicoes-prontas",
            ["refeicoes"]           = "refeicoes-prontas",
            ["pizza-congelada"]     = "refeicoes-prontas",
            ["lasanha"]             = "refeicoes-prontas",

            // ── Higiene Pessoal ────────────────────────────────────────────────
            ["higiene"]             = "higiene-pessoal",
            ["sabonete"]            = "higiene-pessoal",
            ["shampoo"]             = "higiene-pessoal",
            ["gel-duche"]           = "higiene-pessoal",

            // ── Higiene Oral ───────────────────────────────────────────────────
            ["dental"]              = "higiene-oral",
            ["pasta-dentes"]        = "higiene-oral",
            ["escova-dentes"]       = "higiene-oral",

            // ── Detergentes ────────────────────────────────────────────────────
            ["detergente"]          = "detergentes",
            ["detergentes"]         = "detergentes",
            ["lava-roupa"]          = "detergentes",
            ["lava-loica"]          = "detergentes",

            // ── Limpeza do Lar ─────────────────────────────────────────────────
            ["limpeza"]             = "limpeza-lar",
        };

    /// <summary>
    /// Maps a scraped category string to a canonical slug.
    /// Returns <c>null</c> if no mapping is found.
    /// </summary>
    public static string? MapToSlug(string? scrapedCategory)
    {
        if (string.IsNullOrWhiteSpace(scrapedCategory))
            return null;

        // Normalise: lowercase, trim, replace underscores and spaces with hyphens,
        // strip accents so that "água" and "agua" both normalise to "agua".
        var normalized = NormalizeInput(scrapedCategory);

        // 1. Exact match on the full normalised string
        if (_map.TryGetValue(normalized, out var exactSlug))
            return exactSlug;

        // 2. The scraped value may be a path like "mercearia/arroz" or
        //    a compound like "produtos-lacteos/leite-uht". Try each segment.
        var segments = normalized.Split(['/', '|'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 1)
        {
            // Last segment is usually the most specific
            foreach (var seg in segments.Reverse())
            {
                if (_map.TryGetValue(seg, out var segSlug))
                    return segSlug;
            }
        }

        // 3. Keyword containment: does the input contain any known token?
        foreach (var (key, slug) in _map)
        {
            if (normalized.Contains(key, StringComparison.OrdinalIgnoreCase))
                return slug;
        }

        return null;
    }

    private static string NormalizeInput(string input)
    {
        // Remove accents using Unicode normalization
        var withoutAccents = RemoveAccents(input);
        return withoutAccents
            .ToLowerInvariant()
            .Trim()
            .Replace('_', '-')
            .Replace(' ', '-');
    }

    private static string RemoveAccents(string input)
    {
        var normalized = input.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) !=
                System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
    }
}
