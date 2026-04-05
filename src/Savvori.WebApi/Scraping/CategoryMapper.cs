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

            // ── Extra Laticínios / Dairy ───────────────────────────────────────
            ["lacticinio"]          = "laticinios",
            ["lacticinios"]         = "laticinios",
            ["bebida-lactea"]       = "laticinios",
            ["sobremesa-lactea"]    = "iogurtes",
            ["sobremesas-lacteas"]  = "iogurtes",
            ["nata"]                = "natas-cremes",
            ["queijinho"]           = "queijos",

            // ── Extra Frescos / Fresh ──────────────────────────────────────────
            ["leguminosas"]         = "legumes",
            ["tubarculos"]          = "legumes",
            ["tuberculos"]         = "legumes",
            ["cogumelos"]           = "legumes",
            ["ervas-aromaticas"]    = "legumes",
            ["saladas"]             = "legumes",
            ["aves"]                = "carne",
            ["peru"]                = "carne",
            ["vitela"]              = "carne",
            ["carne-picada"]        = "carne",
            ["hamburgueres"]        = "carne",
            ["salsichas"]           = "charcutaria",
            ["presunto"]            = "charcutaria",
            ["ovo"]                 = "ovos",
            ["peixe-fresco"]        = "peixe-marisco",
            ["marisco-fresco"]      = "peixe-marisco",
            ["salmao"]              = "peixe-marisco",
            ["camarao"]             = "peixe-marisco",
            ["lulas"]               = "peixe-marisco",

            // ── Extra Mercearia / Grocery ──────────────────────────────────────
            ["leguminosas-secas"]   = "conservas",
            ["feijao"]              = "conservas",
            ["grao"]                = "conservas",
            ["lentilhas"]           = "conservas",
            ["vinagre"]             = "molhos-temperos",
            ["sal"]                 = "molhos-temperos",
            ["acucar"]              = "mercearia",
            ["farinha"]             = "mercearia",
            ["fermento"]            = "mercearia",
            ["cacau"]               = "mercearia",
            ["chocolate"]           = "bolachas",
            ["snack"]               = "bolachas",
            ["snacks"]              = "bolachas",
            ["frutos-secos"]        = "bolachas",
            ["amendoins"]           = "bolachas",
            ["barras-cereais"]      = "cereais",
            ["papas"]               = "cereais",
            ["alimentacao-infantil"]= "cereais",
            ["massas-frescas"]      = "massas",
            ["lasanha-seca"]        = "massas",
            ["caldo"]               = "molhos-temperos",
            ["caldos"]              = "molhos-temperos",
            ["sopa"]                = "conservas",
            ["sopas"]               = "conservas",
            ["azeites"]             = "azeite-oleos",

            // ── Extra Padaria / Bakery ─────────────────────────────────────────
            ["tostas"]              = "pao",
            ["torradas"]            = "pao",
            ["pao-de-forma"]        = "pao",
            ["croissant"]           = "bolos-sobremesas",
            ["pastel"]              = "bolos-sobremesas",
            ["gelado"]              = "bolos-sobremesas",
            ["gelados"]             = "bolos-sobremesas",
            ["mousse"]              = "bolos-sobremesas",

            // ── Extra Bebidas / Drinks ─────────────────────────────────────────
            ["aguas"]               = "agua",
            ["agua-com-gas"]        = "agua",
            ["agua-mineral"]        = "agua",
            ["aguas-aromatizadas"]  = "agua",
            ["refrigerante"]        = "sumos",
            ["refrigerantes"]       = "sumos",
            ["sumo-laranja"]        = "sumos",
            ["bebida-energetica"]   = "sumos",
            ["cha"]                 = "sumos",
            ["chás"]                = "sumos",
            ["cafe"]                = "mercearia",
            ["cafes"]               = "mercearia",
            ["capsula"]             = "mercearia",
            ["capsulas"]            = "mercearia",
            ["vinho-tinto"]         = "bebidas-alcoolicas",
            ["vinho-branco"]        = "bebidas-alcoolicas",
            ["vinho-verde"]         = "bebidas-alcoolicas",
            ["espumante"]           = "bebidas-alcoolicas",
            ["sidra"]               = "bebidas-alcoolicas",
            ["whisky"]              = "bebidas-alcoolicas",
            ["gin"]                 = "bebidas-alcoolicas",
            ["licor"]               = "bebidas-alcoolicas",

            // ── Extra Congelados / Frozen ──────────────────────────────────────
            ["alimentos-congelados"]= "congelados",
            ["frango-congelado"]    = "legumes-congelados",
            ["carne-congelada"]     = "legumes-congelados",
            ["sobremesas-congeladas"]= "bolos-sobremesas",

            // ── Extra Higiene / Personal care ─────────────────────────────────
            ["higiene-feminina"]    = "higiene-pessoal",
            ["fraldas"]             = "higiene-pessoal",
            ["desodorizante"]       = "higiene-pessoal",
            ["creme-corporal"]      = "higiene-pessoal",
            ["perfume"]             = "higiene-pessoal",
            ["maquiagem"]           = "higiene-pessoal",
            ["maquilhagem"]         = "higiene-pessoal",
            ["fio-dental"]          = "higiene-oral",
            ["colutorio"]           = "higiene-oral",
            ["elixir"]              = "higiene-oral",

            // ── Extra Limpeza / Cleaning ──────────────────────────────────────
            ["amaciador"]           = "detergentes",
            ["amaciadores"]         = "detergentes",
            ["lava-loicas"]         = "detergentes",
            ["capsulas-lavar"]      = "detergentes",
            ["desengordurante"]     = "limpeza-lar",
            ["desinfetante"]        = "limpeza-lar",
            ["multiusos"]           = "limpeza-lar",
            ["papel-cozinha"]       = "limpeza-lar",
            ["papel-higienico"]     = "limpeza-lar",
            ["esponjas"]            = "limpeza-lar",

            // ── Chocolates e Confeitaria (snacks, sweets) ──────────────────────
            ["pepitas,-recheadas-e-cobertas"] = "bolachas",
            ["pepitas"]             = "bolachas",
            ["tabletes"]            = "bolachas",
            ["wafers"]              = "bolachas",
            ["gomas"]               = "bolachas",
            ["bombons"]             = "bolachas",
            ["pipocas"]             = "bolachas",
            ["pedacos"]             = "bolachas",
            ["rebucados,-pastilhas-e-chupas"] = "bolachas",
            ["rebucados"]           = "bolachas",
            ["pastilhas"]           = "bolachas",
            ["batatas-fritas"]      = "bolachas",
            ["fibra,-tortitas-e-marinheiras"] = "bolachas",
            ["tortitas"]            = "bolachas",
            ["marinheiras"]         = "bolachas",

            // ── Iogurtes subcategorias ────────────────────────────────────────
            ["liquidos"]            = "iogurtes",
            ["bifidus-e-funcionais"] = "iogurtes",
            ["bifidus"]             = "iogurtes",
            ["linha-e-fibra"]       = "iogurtes",
            ["magros"]              = "iogurtes",
            ["naturais"]            = "iogurtes",
            ["gregos"]              = "iogurtes",
            ["aromas"]              = "iogurtes",
            ["aromas-e-toppings"]   = "iogurtes",
            ["kefir"]               = "iogurtes",
            ["base-vegetal"]        = "iogurtes",

            // ── Queijos subcategorias ─────────────────────────────────────────
            ["ralado"]              = "queijos",
            ["fresco-e-requeijao"]  = "queijos",
            ["requeijao"]           = "queijos",

            // ── Manteiga e Margarinas subcategorias ───────────────────────────
            ["para-barrar"]         = "manteiga-margarinas",

            // ── Charcutaria subcategorias ─────────────────────────────────────
            ["outros-enchidos-e-fumeiro"] = "charcutaria",
            ["enchidos"]            = "charcutaria",
            ["fumeiro"]             = "charcutaria",
            ["pates-e-pastas"]      = "charcutaria",
            ["pates"]               = "charcutaria",

            // ── Carne subcategorias ───────────────────────────────────────────
            ["novilho-e-bovino"]    = "carne",
            ["novilho"]             = "carne",
            ["bovino"]              = "carne",
            ["angus"]               = "carne",

            // ── Peixe e Marisco subcategorias ─────────────────────────────────
            ["pescada"]             = "peixe-marisco",
            ["dourada-e-robalo"]    = "peixe-marisco",
            ["dourada"]             = "peixe-marisco",
            ["robalo"]              = "peixe-marisco",

            // ── Frutas subcategorias ──────────────────────────────────────────
            ["melao,-melancia-e-meloa"] = "frutas",
            ["melao"]               = "frutas",
            ["melancia"]            = "frutas",
            ["meloa"]               = "frutas",
            ["laranjas,-tangerinas-e-limoes"] = "frutas",
            ["laranjas"]            = "frutas",
            ["tangerinas"]          = "frutas",
            ["limoes"]              = "frutas",
            ["bananas,-peras-e-macas"] = "frutas",
            ["bananas"]             = "frutas",
            ["peras"]               = "frutas",
            ["macas"]               = "frutas",

            // ── Legumes subcategorias ─────────────────────────────────────────
            ["batatas,-cebolas-e-alhos"] = "legumes",
            ["cebolas"]             = "legumes",
            ["alhos"]               = "legumes",
            ["tomates,-pepinos-e-pimentos"] = "legumes",
            ["tomates"]             = "legumes",
            ["pepinos"]             = "legumes",
            ["pimentos"]            = "legumes",
            ["cenouras,-couves-e-brocolos"] = "legumes",
            ["cenouras"]            = "legumes",
            ["couves"]              = "legumes",
            ["brocolos"]            = "legumes",
            ["alfaces"]             = "legumes",
            ["batatas-e-pure"]      = "legumes",

            // ── Massas subcategorias ──────────────────────────────────────────
            ["noodles"]             = "massas",

            // ── Conservas subcategorias ───────────────────────────────────────
            ["compotas-e-doces"]    = "conservas",
            ["compotas"]            = "conservas",
            ["polpas-e-concentrados"] = "conservas",
            ["polpas"]              = "conservas",
            ["azeitonas,-pickles-e-tremocos"] = "conservas",
            ["azeitonas"]           = "conservas",
            ["pickles"]             = "conservas",
            ["tremocos"]            = "conservas",
            ["pure"]                = "conservas",

            // ── Pão subcategorias ─────────────────────────────────────────────
            ["fatiado-e-bola"]      = "pao",
            ["fatiado"]             = "pao",

            // ── Bolos e Sobremesas subcategorias ──────────────────────────────
            ["gelatina"]            = "bolos-sobremesas",
            ["gelatinas"]           = "bolos-sobremesas",

            // ── Refeições Prontas subcategorias ───────────────────────────────
            ["pizzas"]              = "refeicoes-prontas",

            // ── Cereais subcategorias (incl. infantis) ────────────────────────
            ["infantis"]            = "cereais",
            ["infantis-e-juvenis"]  = "cereais",
            ["barras,-sandwichs-e-bites"] = "cereais",

            // ── Bebidas subcategorias ─────────────────────────────────────────
            ["energeticas-e-isotonicas"] = "sumos",
            ["energeticas"]         = "sumos",
            ["isotonicas"]          = "sumos",
            ["achocolatados"]       = "sumos",
            ["bebida-de-soja"]      = "sumos",
            ["bebida-de-aveia"]     = "sumos",
            ["bebida-de-amendoa"]   = "sumos",

            // ── Congelados subcategorias ──────────────────────────────────────
            ["gelo"]                = "congelados",

            // ── Mercearia miscelânea e marcas/origens ─────────────────────────
            ["mel"]                 = "mercearia",
            ["proteina"]            = "mercearia",
            ["nacional"]            = "mercearia",
            ["estrangeiro"]         = "mercearia",
            ["produtos-biologicos"] = "mercearia",
            ["pingo-doce"]          = "mercearia",
            ["alternativas"]        = "mercearia",
            ["individual"]          = "mercearia",

            // ── Limpeza subcategorias ─────────────────────────────────────────
            ["sacos-e-sacos-de-compras"] = "limpeza-lar",
            ["sacos-de-compras"]    = "limpeza-lar",

            // ── Peixe Congelado subcategorias ──────────────────────────────────
            ["douradinhos-e-filetes"] = "peixe-congelado",
            ["douradinhos"]         = "peixe-congelado",

            // ── Refeições Prontas subcategorias ───────────────────────────────
            ["pronto-a-cozinhar"]   = "refeicoes-prontas",
            ["nuggets-e-crocantes"] = "refeicoes-prontas",
            ["nuggets-e-panados"]   = "refeicoes-prontas",
            ["nuggets"]             = "refeicoes-prontas",
            ["panados"]             = "refeicoes-prontas",
            ["crocantes"]           = "refeicoes-prontas",

            // ── Conservas subcategorias adicionais ────────────────────────────
            ["tomate-polpa,-pelado-e-seco"] = "conservas",
            ["tomate-polpa"]        = "conservas",
            ["acompanhamentos"]     = "conservas",
            ["pastas-e-dips"]       = "conservas",

            // ── Charcutaria subcategorias adicionais ──────────────────────────
            ["curado"]              = "charcutaria",
            ["bacon-e-fumados"]     = "charcutaria",
            ["bacon"]               = "charcutaria",
            ["alheira-e-farinheira"] = "charcutaria",
            ["alheira"]             = "charcutaria",
            ["farinheira"]          = "charcutaria",
            ["fresco-e-cozido"]     = "charcutaria",
            ["tradicionais"]        = "charcutaria",

            // ── Frutas subcategorias adicionais ───────────────────────────────
            ["banana,-maca-e-pera"] = "frutas",
            ["uvas-e-tropicais"]    = "frutas",
            ["uvas"]                = "frutas",
            ["tropicais"]           = "frutas",
            ["laranja,-clementina-e-limao"] = "frutas",
            ["clementina"]          = "frutas",
            ["pessego,-ameixa-e-kiwi"] = "frutas",
            ["pessego"]             = "frutas",
            ["ameixa"]              = "frutas",
            ["kiwi"]                = "frutas",

            // ── Legumes subcategorias adicionais ──────────────────────────────
            ["alface,-tomate,-pepino-e-pimento"] = "legumes",
            ["cebola,-alho-e-nabo"] = "legumes",
            ["nabo"]                = "legumes",
            ["cenoura,-abobora-e-beterraba"] = "legumes",
            ["abobora"]             = "legumes",
            ["beterraba"]           = "legumes",
            ["batata,-batata-doce-e-mandioca"] = "legumes",
            ["batata-doce"]         = "legumes",
            ["mandioca"]            = "legumes",

            // ── Carne subcategorias adicionais ────────────────────────────────
            ["pato-e-coelho"]       = "carne",
            ["pato"]                = "carne",
            ["coelho"]              = "carne",

            // ── Peixe e Marisco subcategorias adicionais ──────────────────────
            ["filetes,-lombos-e-postas"] = "peixe-marisco",
            ["lombos"]              = "peixe-marisco",
            ["postas"]              = "peixe-marisco",

            // ── Iogurtes subcategorias adicionais ─────────────────────────────
            ["vegegurtes-e-yofu"]   = "iogurtes",
            ["vegegurtes"]          = "iogurtes",
            ["yofu"]                = "iogurtes",

            // ── Arroz e Outros Grãos ───────────────────────────────────────────
            ["couscous,-quinoa,-bulgur-e-outros"] = "arroz",
            ["couscous"]            = "arroz",
            ["quinoa"]              = "arroz",
            ["bulgur"]              = "arroz",

            // ── Massas subcategorias adicionais ───────────────────────────────
            ["italianas"]           = "massas",

            // ── Cereais subcategorias adicionais ──────────────────────────────
            ["corn-flakes"]         = "cereais",
            ["aveia,-muesli-e-preparados"] = "cereais",
            ["aveia"]               = "cereais",
            ["muesli"]              = "cereais",

            // ── Bolachas / Snacks subcategorias adicionais ────────────────────
            ["aperitivos"]          = "bolachas",
            ["tabuas-e-aperitivos"] = "bolachas",
            ["sementes-e-pevides"]  = "bolachas",
            ["sementes"]            = "bolachas",
            ["pevides"]             = "bolachas",
            ["sem-batata"]          = "bolachas",

            // ── Pão subcategorias adicionais ──────────────────────────────────
            ["mini-bites-e-sandwich"] = "pao",

            // ── Bolos e Sobremesas subcategorias adicionais ───────────────────
            ["crepes-e-petit-gateau"] = "bolos-sobremesas",
            ["crepes"]              = "bolos-sobremesas",
            ["tartes-geladas-e-viennettas"] = "bolos-sobremesas",
            ["tartes"]              = "bolos-sobremesas",
            ["folhados"]            = "bolos-sobremesas",

            // ── Bebidas Alcoólicas subcategorias adicionais ───────────────────
            ["rum"]                 = "bebidas-alcoolicas",
            ["vodka"]               = "bebidas-alcoolicas",
            ["tequila"]             = "bebidas-alcoolicas",
            ["sangrias-e-aromatizados"] = "bebidas-alcoolicas",
            ["sangria"]             = "bebidas-alcoolicas",
            ["lbv"]                 = "bebidas-alcoolicas",
            ["cocktails"]           = "bebidas-alcoolicas",
            ["cocktail"]            = "bebidas-alcoolicas",

            // ── Bebidas Vegetais (variantes sem «de») ──────────────────────────
            ["bebida-soja"]         = "sumos",
            ["bebida-aveia"]        = "sumos",
            ["bebida-amendoa"]      = "sumos",

            // ── Laticínios subcategorias adicionais ───────────────────────────
            ["alimentos-lacteos"]   = "laticinios",

            // ── Frescos (fallback genérico) ────────────────────────────────────
            ["fresco"]              = "frescos",

            // ── Mercearia miscelânea adicional ────────────────────────────────
            ["seco"]                = "mercearia",
            ["vegetariano-e-vegan"] = "mercearia",
            ["vegetariano"]         = "mercearia",
            ["vegan"]               = "mercearia",
            ["tofu-e-seitan"]       = "mercearia",
            ["tofu"]                = "mercearia",
            ["seitan"]              = "mercearia",
            ["cozinhas-do-mundo"]   = "mercearia",
            ["food-lab"]            = "mercearia",
            ["produtos-gourmet"]    = "mercearia",

            // ── Higiene Pessoal subcategorias adicionais ──────────────────────
            ["gel-de-banho"]        = "higiene-pessoal",
            ["cabelo-e-perfumaria"] = "higiene-pessoal",
            ["cabelo"]              = "higiene-pessoal",
            ["perfumaria"]          = "higiene-pessoal",
            ["protecao-e-hidratacao"] = "higiene-pessoal",
            ["toalhas-de-banho"]    = "higiene-pessoal",
            ["toalhitas"]           = "higiene-pessoal",
            ["cotonetes-e-soro-fisiologico"] = "higiene-pessoal",
            ["cotonetes"]           = "higiene-pessoal",

            // ── Limpeza do Lar subcategorias adicionais ───────────────────────
            ["arrumacao-e-organizacao"] = "limpeza-lar",
            ["pratos,-copos-e-talheres"] = "limpeza-lar",

            // ── Saúde e Bem-Estar ─────────────────────────────────────────────
            ["suplementos-e-vitaminas"] = "saude-bem-estar",
            ["suplementos"]         = "saude-bem-estar",
            ["vitaminas"]           = "saude-bem-estar",
            ["outros-suplementos"]  = "saude-bem-estar",
            ["suplementacao"]       = "saude-bem-estar",
            ["multivitaminicos"]    = "saude-bem-estar",
            ["controlo-de-peso-e-drenantes"] = "saude-bem-estar",
            ["drenantes"]           = "saude-bem-estar",
            ["saude-e-bem-estar"]   = "saude-bem-estar",
            ["cuidados-de-saude"]   = "saude-bem-estar",

            // ── Bebé e Puericultura ───────────────────────────────────────────
            ["carrinhos-de-passeio"] = "bebe-puericultura",
            ["camas,-bercos-e-colchoes"] = "bebe-puericultura",
            ["bercos"]              = "bebe-puericultura",
            ["gravidez-e-puericultura"] = "bebe-puericultura",
            ["gravidez"]            = "bebe-puericultura",
            ["puericultura"]        = "bebe-puericultura",
            ["cadeiras-e-assentos-auto"] = "bebe-puericultura",
            ["banheiras-e-acessorios"] = "bebe-puericultura",
            ["sacos,-marsupios-e-acessorios"] = "bebe-puericultura",
            ["marsupios"]           = "bebe-puericultura",
            ["brinquedos-de-bebe"]  = "bebe-puericultura",
            ["brinquedos"]          = "bebe-puericultura",
            ["biberoes-e-tetinas"]  = "bebe-puericultura",
            ["biberoes"]            = "bebe-puericultura",
            ["tetinas"]             = "bebe-puericultura",
            ["mobiliario-didatico"] = "bebe-puericultura",
            ["chupetas-e-mordedores"] = "bebe-puericultura",
            ["chupetas"]            = "bebe-puericultura",
            ["mordedores"]          = "bebe-puericultura",
            ["sacos-de-dormir,-almofadas-e-ninhos"] = "bebe-puericultura",
            ["sacos-de-dormir"]     = "bebe-puericultura",
            ["bacios-e-redutores"]  = "bebe-puericultura",
            ["bacios"]              = "bebe-puericultura",
            ["livros-para-bebe"]    = "bebe-puericultura",
            ["livros-de-atividades-e-colorir"] = "bebe-puericultura",
            ["livros-de-bebe"]      = "bebe-puericultura",
            ["peluches-interativos"] = "bebe-puericultura",
            ["peluches"]            = "bebe-puericultura",
            ["babetes"]             = "bebe-puericultura",
            ["espreguicadeiras,-parques-e-andadores"] = "bebe-puericultura",
            ["espreguicadeiras"]    = "bebe-puericultura",
            ["andadores"]           = "bebe-puericultura",
            ["resguardos"]          = "bebe-puericultura",
            ["lencois"]             = "bebe-puericultura",
            ["almofadas-e-ninhos"]  = "bebe-puericultura",
            ["almofadas"]           = "bebe-puericultura",
            ["decoracao-e-seguranca"] = "bebe-puericultura",
            ["roupa-de-cama"]       = "bebe-puericultura",
            ["crianca"]             = "bebe-puericultura",
            ["bebe"]                = "bebe-puericultura",
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
