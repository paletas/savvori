using System.Text.RegularExpressions;

namespace Savvori.WebApi.Scraping;

/// <summary>One keyword rule of a v1 -> v2 split: a product whose normalised name contains any keyword goes to the target.</summary>
public sealed record SplitRule(string Target, params string[] Keywords);

/// <summary>How one v1 category maps into v2: rules first (in order), then the default (null = leave for the classifier).</summary>
public sealed record LegacyMapping(string? Default, IReadOnlyList<SplitRule> Rules)
{
    public bool IsSplit => Rules.Count > 0 || Default is null;
}

public enum PlacementKind { OneToOne, Rule, LeftForClassifier }

public sealed record Placement(string? TargetSlug, PlacementKind Kind);

/// <summary>
/// The approved v1 -> v2 mapping (docs/TAXONOMY_V2.md). Placement is deterministic: an explicit keyword rule, else the
/// mapping's default, else nothing (the product is left uncategorised for the classifier and the review queue).
/// Nothing here calls a model.
/// </summary>
public static class TaxonomyV2
{
    public static IReadOnlySet<string> V2Slugs { get; } =
        TaxonomyV2Data.Aisles.SelectMany(a => a.Leaves.Select(l => l.Slug)).Concat(TaxonomyV2Data.Aisles.Select(a => a.Slug)).ToHashSet();

    public static IReadOnlySet<string> AisleSlugs { get; } = TaxonomyV2Data.Aisles.Select(a => a.Slug).ToHashSet();

    public static IReadOnlySet<string> V1Slugs { get; } = CategoryTaxonomy.All.Select(d => d.Slug).ToHashSet();

    private static SplitRule R(string target, params string[] keywords) => new(target, keywords);
    private static LegacyMapping One(string target) => new(target, []);
    private static LegacyMapping Split(string? @default, params SplitRule[] rules) => new(@default, rules);

    /// <summary>Every assignable v1 category and where its products go.</summary>
    public static IReadOnlyDictionary<string, LegacyMapping> Legacy { get; } = new Dictionary<string, LegacyMapping>
    {
        ["leite"] = Split("leite", R("bebidas-vegetais", "bebida vegetal", "bebida de soja", "bebida de aveia", "bebida de amendoa", "bebida de arroz", "bebida de coco", "soja", "aveia", "amendoa")),
        ["iogurtes"] = Split("iogurtes", R("sobremesas-lacteas", "pudim", "gelatina", "mousse", "arroz doce", "sobremesa", "leite creme", "flan")),
        ["queijos"] = One("queijos"),
        ["manteiga-margarinas"] = One("manteiga-margarinas"),
        ["natas-cremes"] = One("natas-cremes"),
        ["frutas"] = One("frutas"),
        ["legumes"] = Split("legumes", R("saladas-ervas", "salada", "alface embalada", "ervas", "rebentos", "mistura de salada")),
        ["carne"] = Split(null,
            R("carne-picada-preparados", "picada", "hamburguer", "espetada", "almondega", "marinado", "marinada", "kebab", "salsicha fresca"),
            R("aves", "frango", "peru", "pato", "galinha", "codorniz", "coxa", "peito de frango"),
            R("carne-porco", "porco", "suino", "entremeada", "febras", "entrecosto", "costeleta de porco", "lombo de porco"),
            R("carne-vaca", "vaca", "novilho", "bovino", "bife", "vitela", "posta de vaca")),
        ["peixe-marisco"] = Split(null,
            R("bacalhau-salgados", "bacalhau", "salgado", "salgada", "peixe seco"),
            R("marisco", "camarao", "gamba", "mexilhao", "ameijoa", "lagosta", "polvo", "lula", "choco", "ostra", "berbigao", "sapateira", "santola", "navalheira", "percebes", "caranguejo", "lingueirao", "marisco"),
            R("peixe-fresco", "salmao", "dourada", "robalo", "pescada", "carapau", "cavala fresca", "linguado", "pargo", "sargo", "truta", "peixe espada")),
        ["ovos"] = One("ovos"),
        ["charcutaria"] = Split(null,
            R("fiambre-presunto", "fiambre", "presunto", "paio de lombo"),
            R("pates-cozidos", "pate", "mortadela", "galantina"),
            R("enchidos", "chourico", "alheira", "salsicha", "linguica", "farinheira", "morcela", "paio", "salame", "salpicao", "chorizo")),
        ["arroz"] = Split("arroz", R("sobremesas-lacteas", "arroz doce")),
        ["massas"] = One("massas"),
        ["conservas"] = Split(null,
            R("sopas-pratos-preparados", "sopa", "guisado", "feijoada", "dobrada", "caldo verde"),
            R("conservas-peixe", "atum", "sardinha", "cavala", "anchova", "enguia", "filetes de", "conserva de peixe"),
            R("conservas-vegetais", "grao", "feijao", "milho", "ananas", "cogumelos", "ervilhas", "tomate pelado", "azeitona", "pepino", "alcaparras", "beterraba", "espargos", "macedonia", "calda")),
        ["molhos-temperos"] = Split("molhos-temperos",
            R("sopas-pratos-preparados", "sopa"),
            R("farinhas-preparados", "farinha", "fermento", "pao ralado", "preparado para bolos")),
        ["azeite-oleos"] = One("azeite-oleos"),
        ["cereais"] = One("cereais"),
        ["bolachas"] = Split(null,
            R("bolachas-salgadas", "tostas", "salgadas", "crackers", "cracker", "cream cracker"),
            R("bolachas-recheadas", "recheada", "recheio", "wafer", "wafers", "cream", "sandwich", "oreo"),
            R("bolachas-integrais", "integral", "integrais", "aveia", "cereais", "fibra", "digestive"),
            R("bolachas-simples", "maria", "simples", "leite", "manteiga", "dourada")),
        ["pao"] = Split("pao", R("pao-forma-embalado", "fatiado", "fatiada", "forma", "wrap", "wraps", "tortilha", "tortilhas")),
        ["bolos-sobremesas"] = Split(null,
            R("sobremesas-lacteas", "pudim", "gelatina", "mousse", "arroz doce", "leite creme", "flan"),
            R("sobremesas-preparadas", "sobremesa", "tiramisu", "cheesecake", "brigadeiro", "profiteroles", "semifrio"),
            R("bolos-pastelaria", "bolo", "bolos", "pastel", "pasteis", "croissant", "donut", "donuts", "muffin", "queque", "brioche", "tarte", "folhado", "bola de berlim", "waffle", "panqueca")),
        ["agua"] = One("agua"),
        ["sumos"] = Split("sumos",
            R("energeticas-desporto", "energetica", "red bull", "redbull", "monster", "isotonica", "gatorade", "powerade"),
            R("refrigerantes", "refrigerante", "cola", "coca", "pepsi", "fanta", "sprite", "7up", "limonada", "ice tea", "iced tea", "gasosa", "tonica", "schweppes")),
        ["bebidas-alcoolicas"] = Split(null,
            R("cocktails", "sangria", "cocktail", "mojito", "caipirinha", "spritz", "margarita", "pina colada", "gin tonic"),
            R("cerveja", "cerveja", "sagres", "super bock", "superbock", "heineken", "stella", "corona", "budweiser", "sidra", "imperial", "stout"),
            R("vinho", "vinho", "espumante", "porto", "prosecco", "champagne", "cava", "moscatel", "tinto", "rose"),
            R("espirituosas-licores", "whisky", "vodka", "gin", "rum", "licor", "aguardente", "brandy", "conhaque", "tequila", "amarguinha", "ginjinha", "medronho", "vermute", "aperol", "campari", "bagaco")),
        ["legumes-congelados"] = One("legumes-congelados"),
        ["peixe-congelado"] = One("peixe-marisco-congelado"),
        ["refeicoes-prontas"] = Split("refeicoes-prontas", R("pizzas-salgados", "pizza", "pizzas", "croquete", "croquetes", "rissol", "rissois", "folhado", "empada", "salgados")),
        ["higiene-pessoal"] = Split(null,
            R("protecao-solar", "protetor solar", "protecao solar", "bronzeador", "spf", "after sun", "apos sol", "solar"),
            R("desodorizantes", "desodorizante", "antitranspirante", "roll on", "deo"),
            R("higiene-intima", "intimo", "intima", "penso", "pensos", "tampoes", "absorvente"),
            R("barbear-depilacao", "barbear", "gilette", "lamina", "laminas", "depilacao", "depilatoria", "after shave"),
            R("papel-higienico-lencos", "papel higienico", "lenco", "lencos"),
            R("cabelo", "shampoo", "champo", "condicionador", "mascara capilar", "laca", "coloracao", "capilar", "cabelo"),
            R("banho-higiene", "gel de banho", "sabonete", "banho", "gel duche", "duche"),
            R("cosmetica-rosto-corpo", "creme facial", "creme de rosto", "creme de maos", "hidratante", "serum", "maquilhagem", "batom", "rimel", "verniz", "esfoliante", "locao", "labial", "desmaquilhante")),
        ["higiene-oral"] = One("higiene-oral"),
        ["detergentes"] = Split(null,
            R("detergentes-loica", "loica", "fairy", "finish", "abrilhantador", "sal para maquina"),
            R("detergentes-roupa", "roupa", "amaciador", "ariel", "skip", "cores")),
        ["limpeza-lar"] = Split("limpeza-lar",
            R("ambientadores-inseticidas", "ambientador", "inseticida", "antimosquitos", "mosquito", "baratas", "formigas", "vela perfumada"),
            R("papel-descartaveis", "papel de cozinha", "rolo de cozinha", "guardanapo", "guardanapos", "pelicula", "aluminio", "sacos do lixo", "papel vegetal", "palhinhas", "copos descartaveis")),
        ["bebe-puericultura"] = Split(null,
            R("alimentacao-bebe", "papa", "papas", "leite lactantes", "leite de transicao", "leite de crescimento", "farinha lactea", "boiao", "nutriben", "bledina", "milupa", "aptamil", "lactantes"),
            R("fraldas-higiene-bebe", "fralda", "fraldas", "toalhita", "toalhitas", "pampers", "dodot"),
            R("puericultura-mobiliario", "berco", "carrinho", "cadeira auto", "cadeira de bebe", "alcofa", "andarilho", "chupeta", "biberao", "biberon", "mordedor", "brinquedo", "manta", "almofada", "ninho", "body")),
        ["saude-bem-estar"] = Split(null,
            R("suplementos-bem-estar", "vitamina", "suplemento", "omega", "colageneo", "magnesio", "probiotico", "multivitaminico", "proteina", "whey", "melatonina", "complemento alimentar"),
            R("farmacia-primeiros-socorros", "penso", "pensos rapidos", "desinfetante", "alcool", "termometro", "mascara", "gel desinfetante", "compressa", "ligadura", "paracetamol", "soro fisiologico")),
    };

    /// <summary>
    /// Seed rules for the categories that did not exist in v1 (pet food, coffee, chocolate, sun care, ...). They give the
    /// classifier its first examples, and apply only to products that have no category. Deliberately conservative: a
    /// word like "chocolate" alone is not enough (it would catch a chocolate yoghurt). Order matters: first match wins.
    /// </summary>
    public static IReadOnlyList<SplitRule> Seeds { get; } =
    [
        R("animais-acessorios", "areia para gatos", "areia higienica", "coleira", "aquario", "comida para passaros", "comida para peixes"),
        R("comida-gatos", "comida para gatos", "comida para gato", "racao para gatos", "alimento para gatos", "snack para gatos", "whiskas", "sheba"),
        R("comida-caes", "comida para caes", "comida para cao", "racao para caes", "alimento para caes", "snack para caes", "pedigree", "chappi"),
        R("protecao-solar", "protetor solar", "protecao solar", "bronzeador", "after sun", "spf"),
        R("cosmetica-rosto-corpo", "creme facial", "creme de rosto", "serum", "maquilhagem", "batom", "rimel", "desmaquilhante", "creme de maos", "locao corporal"),
        R("energeticas-desporto", "bebida energetica", "red bull", "monster energy", "isotonica", "gatorade", "powerade"),
        R("bebidas-vegetais", "bebida vegetal", "bebida de soja", "bebida de aveia", "bebida de amendoa", "bebida de arroz", "bebida de coco"),
        R("cerveja", "cerveja", "sidra"),
        R("vinho", "vinho", "espumante", "prosecco", "champagne"),
        R("espirituosas-licores", "whisky", "vodka", "licor", "aguardente"),
        R("cafe", "cafe", "capsulas de cafe", "nespresso", "dolce gusto"),
        R("cha-infusoes", "cha", "infusao", "infusoes", "tisana", "camomila"),
        R("chocolate", "tablete de chocolate", "chocolate negro", "chocolate de leite", "chocolate branco", "bombons", "bombom", "nutella", "cacau em po"),
        R("snacks-salgados", "batatas fritas", "pipocas", "tremocos", "cheetos", "doritos", "pringles", "snack salgado"),
        R("frutos-secos", "amendoim", "amendoas", "nozes", "caju", "pistacio", "avelas", "frutos secos", "passas"),
        R("compotas-mel", "compota", "mel", "geleia", "marmelada", "creme de barrar"),
        R("acucar-adocantes", "acucar branco", "acucar amarelo", "acucar mascavado", "adocante", "stevia"),
        R("leguminosas-graos", "lentilhas", "quinoa", "cuscuz", "bulgur", "grao de bico seco", "feijao seco"),
        R("farinhas-preparados", "farinha", "fermento", "pao ralado", "preparado para bolos", "levedura", "maizena"),
        R("gelados", "gelado", "gelados", "sorvete", "cornetto", "magnum"),
        R("batatas-pre-fritos", "batatas pre fritas", "batatas congeladas", "pre frito"),
        R("puericultura-mobiliario", "berco", "carrinho de bebe", "cadeira auto", "cadeira de bebe", "alcofa", "andarilho"),
        R("cozinha-mesa", "frigideira", "tacho", "panela", "talheres", "tupperware", "caixa hermetica", "tabuleiro", "cafeteira"),
        R("papelaria-livros", "livro", "caderno", "caneta", "lapis", "esferografica", "agrafador", "cola escolar", "papel a4"),
    ];

    /// <summary>The v2 category a product with NO category can be seeded into by name, or null.</summary>
    public static string? Seed(string productName)
    {
        var text = ProductNormalizer.Normalize(productName);
        foreach (var rule in Seeds)
            if (GetRegexes("seed", rule).Any(r => r.IsMatch(text))) return rule.Target;
        return null;
    }

    private static readonly Dictionary<string, Regex[]> RuleRegex = new();

    private static Regex Compile(string keyword) =>
        new($@"\b{Regex.Escape(keyword)}\b", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Where a product of the given v1 category goes. Parent (aisle-level) categories and unknown slugs return null
    /// so callers decide. <paramref name="productName"/> is matched after accent-stripping and lower-casing.
    /// </summary>
    public static Placement? Place(string legacySlug, string productName)
    {
        if (!Legacy.TryGetValue(legacySlug, out var mapping)) return null;
        var text = ProductNormalizer.Normalize(productName);
        foreach (var rule in mapping.Rules)
        {
            var regexes = GetRegexes(legacySlug, rule);
            if (regexes.Any(r => r.IsMatch(text))) return new Placement(rule.Target, PlacementKind.Rule);
        }
        return mapping.Default is { } d
            ? new Placement(d, PlacementKind.OneToOne)
            : new Placement(null, PlacementKind.LeftForClassifier);
    }

    private static Regex[] GetRegexes(string legacySlug, SplitRule rule)
    {
        var key = $"{legacySlug}>{rule.Target}";
        lock (RuleRegex)
        {
            if (!RuleRegex.TryGetValue(key, out var r))
                RuleRegex[key] = r = rule.Keywords.Select(Compile).ToArray();
            return r;
        }
    }

    /// <summary>The v2 slug for a category the scraper-time rule mapper produced (v1 slug), given the product name.</summary>
    public static string? ResolveForScraper(string v1Slug, string productName) =>
        ResolveMapped(v1Slug, productName) ?? Seed(productName);

    private static string? ResolveMapped(string v1Slug, string productName)
    {
        if (AisleSlugs.Contains(v1Slug)) return null;
        if (V2Slugs.Contains(v1Slug) && !Legacy.ContainsKey(v1Slug)) return v1Slug;
        var placement = Place(v1Slug, productName);
        return placement?.TargetSlug;
    }
}
