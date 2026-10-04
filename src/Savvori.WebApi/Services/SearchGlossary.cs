using Savvori.WebApi.Scraping;

namespace Savvori.WebApi.Services;

/// <summary>
/// A small, curated English/Portuguese grocery glossary used to make product search language-agnostic:
/// searching any member of a group finds products named with any other member ("rice" finds "Arroz Carolino").
/// Deterministic and free at request time, unlike <c>ProductSearchAlias</c> rows, which are model-made and
/// only as complete as the last alias scan. Each group is a set of equivalent terms (plurals listed
/// explicitly, because glossary terms are matched as whole words so "ovo" cannot hit "novo" nor "sal" "salada").
/// Terms are written the way people type them; they are folded (accent-free, lower-case) on load.
/// Only add terms that mean the same product in both languages; leave ambiguous words out
/// (PT "pasta" is spread or toothpaste, EN "pasta" is "massa"), because a wrong group adds noise to every search.
/// </summary>
public static class SearchGlossary
{
    // Longest phrase in the glossary, in words; the query parser tries phrases up to this length first.
    public const int MaxPhraseWords = 4;

    private static readonly string[][] Groups =
    [
        // Staples and pantry
        ["rice", "arroz", "arrozes"],
        ["sugar", "açúcar", "acucar", "açucar"],
        ["salt", "sal"],
        ["flour", "farinha", "farinhas"],
        ["oil", "óleo", "oleo", "óleos", "oleos"],
        ["olive oil", "azeite", "azeites"],
        ["vinegar", "vinagre", "vinagres"],
        ["honey", "mel"],
        ["jam", "compota", "compotas", "geleia", "geleias"],
        ["oats", "oat", "aveia"],
        ["cereal", "cereals", "cereais"],
        ["spaghetti", "esparguete", "esparguetes"],
        ["yeast", "fermento"],
        ["breadcrumbs", "pão ralado", "pao ralado"],
        ["mayonnaise", "maionese"],
        ["mustard", "mostarda"],
        ["olive", "olives", "azeitona", "azeitonas"],
        ["tomato sauce", "molho de tomate"],
        ["canned", "conserva", "conservas"],
        ["tuna", "atum"],
        ["sardine", "sardines", "sardinha", "sardinhas"],
        ["cod", "bacalhau"],

        // Bread, bakery, sweets
        ["bread", "pão", "pao", "pães", "paes"],
        ["toast", "torrada", "torradas"],
        ["cake", "bolo", "bolos"],
        ["cookie", "cookies", "biscuit", "biscuits", "bolacha", "bolachas", "biscoito", "biscoitos"],
        ["ice cream", "gelado", "gelados"],
        ["butter", "manteiga"],
        ["chocolate", "chocolates"],

        // Dairy and eggs
        ["milk", "leite", "leites"],
        ["condensed milk", "leite condensado"],
        ["cheese", "queijo", "queijos"],
        ["yogurt", "yoghurt", "yogurts", "iogurte", "iogurtes"],
        ["cream", "nata", "natas", "creme"],
        ["egg", "eggs", "ovo", "ovos"],

        // Meat and fish
        ["chicken", "frango", "galinha"],
        ["chicken breast", "peito de frango"],
        ["pork", "porco", "suíno", "suino"],
        ["beef", "carne de vaca", "novilho", "bovino"],
        ["veal", "vitela"],
        ["lamb", "borrego", "cordeiro"],
        ["turkey", "peru"],
        ["rabbit", "coelho"],
        ["ham", "fiambre", "presunto"],
        ["bacon", "toucinho"],
        ["sausage", "sausages", "salsicha", "salsichas"],
        ["steak", "steaks", "bife", "bifes"],
        ["minced meat", "ground beef", "carne picada"],
        ["burger", "burgers", "hambúrguer", "hamburguer", "hambúrgueres", "hamburgueres"],
        ["wings", "asas"],
        ["fish", "peixe", "peixes"],
        ["salmon", "salmão", "salmao"],
        ["shrimp", "prawn", "prawns", "camarão", "camarao", "gamba", "gambas"],
        ["octopus", "polvo"],
        ["squid", "lula", "lulas"],
        ["hake", "pescada"],
        ["mackerel", "cavala"],
        ["sea bass", "robalo"],
        ["sea bream", "dourada"],

        // Fruit
        ["apple", "apples", "maçã", "maca", "maçãs", "macas"],
        ["orange", "oranges", "laranja", "laranjas"],
        ["lemon", "lemons", "limão", "limao", "limões", "limoes"],
        ["strawberry", "strawberries", "morango", "morangos"],
        ["grape", "grapes", "uva", "uvas"],
        ["pear", "pears", "pera", "peras"],
        ["peach", "peaches", "pêssego", "pessego", "pêssegos", "pessegos"],
        ["pineapple", "ananás", "ananas"],
        ["watermelon", "melancia"],
        ["melon", "melão", "melao"],
        ["cherry", "cherries", "cereja", "cerejas"],
        ["avocado", "abacate"],
        ["coconut", "coco"],
        ["raisins", "raisin", "passas", "passa"],
        ["fruit", "fruits", "fruta", "frutas"],

        // Vegetables
        ["potato", "potatoes", "batata", "batatas"],
        ["sweet potato", "batata doce", "batata-doce"],
        ["tomato", "tomatoes", "tomate", "tomates"],
        ["onion", "onions", "cebola", "cebolas"],
        ["garlic", "alho", "alhos"],
        ["carrot", "carrots", "cenoura", "cenouras"],
        ["lettuce", "alface", "alfaces"],
        ["cucumber", "pepino", "pepinos"],
        ["zucchini", "courgette", "curgete", "curgetes"],
        ["pumpkin", "abóbora", "abobora"],
        ["eggplant", "aubergine", "beringela", "beringelas"],
        ["bell pepper", "pimento", "pimentos"],
        ["spinach", "espinafre", "espinafres"],
        ["broccoli", "brócolos", "brocolos", "brócolo"],
        ["cabbage", "couve", "couves"],
        ["mushroom", "mushrooms", "cogumelo", "cogumelos"],
        ["corn", "milho"],
        ["beans", "bean", "feijão", "feijao", "feijões", "feijoes"],
        ["chickpeas", "chickpea", "grão de bico", "grao de bico", "grão-de-bico"],
        ["lentils", "lentil", "lentilhas", "lentilha"],
        ["peas", "ervilhas", "ervilha"],
        ["vegetables", "vegetable", "legumes", "legume", "vegetais"],

        // Nuts
        ["nuts", "frutos secos"],
        ["almond", "almonds", "amêndoa", "amendoa", "amêndoas", "amendoas"],
        ["walnut", "walnuts", "noz", "nozes"],
        ["peanut", "peanuts", "amendoim", "amendoins"],
        ["hazelnut", "hazelnuts", "avelã", "avela", "avelãs", "avelas"],
        ["cashew", "cashews", "caju", "cajus"],

        // Herbs and spices
        ["pepper", "pimenta"],
        ["cinnamon", "canela"],
        ["parsley", "salsa"],
        ["coriander", "cilantro", "coentros", "coentro"],
        ["basil", "manjericão", "manjericao"],
        ["ginger", "gengibre"],
        ["turmeric", "açafrão", "acafrao"],
        ["oregano", "orégãos", "oregaos", "orégão"],
        ["bay leaf", "louro"],

        // Drinks
        ["water", "água", "agua"],
        ["sparkling water", "água com gás", "agua com gas"],
        ["juice", "juices", "sumo", "sumos"],
        ["beer", "beers", "cerveja", "cervejas"],
        ["wine", "wines", "vinho", "vinhos"],
        ["coffee", "café", "cafe", "cafés", "cafes"],
        ["tea", "chá", "cha", "chás", "chas"],
        ["soda", "soft drink", "refrigerante", "refrigerantes"],

        // Household and personal care
        ["toilet paper", "papel higiénico", "papel higienico"],
        ["kitchen roll", "paper towel", "rolo de cozinha", "papel de cozinha"],
        ["napkins", "napkin", "guardanapos", "guardanapo"],
        ["diaper", "diapers", "nappy", "nappies", "fralda", "fraldas"],
        ["soap", "sabonete", "sabonetes", "sabão", "sabao"],
        ["toothpaste", "pasta de dentes", "dentífrico", "dentifrico"],
        ["dog", "cão", "cao", "cães", "caes"],
        ["cat", "gato", "gatos"],
        ["baby", "bebé", "bebe", "bebés", "bebes"],

        // Qualifiers people type as words
        ["frozen", "congelado", "congelados", "congelada", "congeladas"],
        ["fresh", "fresco", "frescos", "fresca", "frescas"],
        ["organic", "bio", "biológico", "biologico", "biológica", "biologica"],
        ["whole wheat", "wholegrain", "integral", "integrais"],
        ["whole milk", "leite integral"],
        ["gluten free", "sem glúten", "sem gluten"],
        ["lactose free", "sem lactose"],
        ["sugar free", "sem açúcar", "sem acucar"],
        ["skimmed", "magro"],
        ["semi skimmed", "meio gordo"],
    ];

    private static readonly Dictionary<string, string[]> ByTerm = Build();

    /// <summary>The folded members of the group <paramref name="term"/> belongs to (including itself), or null.</summary>
    public static IReadOnlyList<string>? Lookup(string foldedTerm) =>
        ByTerm.TryGetValue(foldedTerm, out var group) ? group : null;

    /// <summary>Every group, folded and de-duplicated (used by tests to check the glossary stays consistent).</summary>
    public static IReadOnlyList<string[]> FoldedGroups() => ByTerm.Values.Distinct().ToList();

    private static Dictionary<string, string[]> Build()
    {
        var map = new Dictionary<string, string[]>();
        foreach (var raw in Groups)
        {
            var folded = raw.Select(ProductNormalizer.Normalize).Where(t => t.Length > 0).Distinct().ToArray();
            foreach (var term in folded)
            {
                // A term in two groups would silently merge unrelated concepts; fail loudly at startup instead.
                if (!map.TryAdd(term, folded))
                    throw new InvalidOperationException($"Search glossary term '{term}' appears in more than one group.");
            }
        }
        return map;
    }
}
