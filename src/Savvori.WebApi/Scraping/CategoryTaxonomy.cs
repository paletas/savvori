namespace Savvori.WebApi.Scraping;

public record CategoryDefinition(string Name, string Slug, string? ParentSlug);

public static class CategoryTaxonomy
{
    public static IReadOnlyList<CategoryDefinition> All { get; } =
    [
        // ── Parents ──────────────────────────────────────────────────────────
        new("Laticínios",          "laticinios",        null),
        new("Frescos",             "frescos",           null),
        new("Mercearia",           "mercearia",         null),
        new("Padaria e Pastelaria","padaria-pastelaria", null),
        new("Bebidas",             "bebidas",           null),
        new("Congelados",          "congelados",        null),
        new("Higiene e Beleza",    "higiene-beleza",    null),
        new("Limpeza",             "limpeza",           null),

        // ── Laticínios ───────────────────────────────────────────────────────
        new("Leite",                  "leite",              "laticinios"),
        new("Iogurtes",               "iogurtes",           "laticinios"),
        new("Queijos",                "queijos",            "laticinios"),
        new("Manteiga e Margarinas",  "manteiga-margarinas","laticinios"),
        new("Natas e Cremes",         "natas-cremes",       "laticinios"),

        // ── Frescos ──────────────────────────────────────────────────────────
        new("Frutas",             "frutas",         "frescos"),
        new("Legumes e Hortícolas","legumes",        "frescos"),
        new("Carne",              "carne",           "frescos"),
        new("Peixe e Marisco",    "peixe-marisco",   "frescos"),
        new("Ovos",               "ovos",            "frescos"),
        new("Charcutaria",        "charcutaria",     "frescos"),

        // ── Mercearia ────────────────────────────────────────────────────────
        new("Arroz",              "arroz",           "mercearia"),
        new("Massas",             "massas",          "mercearia"),
        new("Conservas",          "conservas",       "mercearia"),
        new("Molhos e Temperos",  "molhos-temperos", "mercearia"),
        new("Azeite e Óleos",     "azeite-oleos",    "mercearia"),
        new("Cereais e Granola",  "cereais",         "mercearia"),
        new("Bolachas e Biscoitos","bolachas",        "mercearia"),

        // ── Padaria e Pastelaria ─────────────────────────────────────────────
        new("Pão",                "pao",                "padaria-pastelaria"),
        new("Bolos e Sobremesas", "bolos-sobremesas",   "padaria-pastelaria"),

        // ── Bebidas ──────────────────────────────────────────────────────────
        new("Água",               "agua",               "bebidas"),
        new("Sumos e Néctares",   "sumos",              "bebidas"),
        new("Bebidas Alcoólicas", "bebidas-alcoolicas", "bebidas"),

        // ── Congelados ───────────────────────────────────────────────────────
        new("Legumes Congelados", "legumes-congelados", "congelados"),
        new("Peixe Congelado",    "peixe-congelado",    "congelados"),
        new("Refeições Prontas",  "refeicoes-prontas",  "congelados"),

        // ── Higiene e Beleza ─────────────────────────────────────────────────
        new("Higiene Pessoal",    "higiene-pessoal",    "higiene-beleza"),
        new("Higiene Oral",       "higiene-oral",       "higiene-beleza"),

        // ── Limpeza ──────────────────────────────────────────────────────────
        new("Detergentes",        "detergentes",        "limpeza"),
        new("Limpeza do Lar",     "limpeza-lar",        "limpeza"),
    ];
}
