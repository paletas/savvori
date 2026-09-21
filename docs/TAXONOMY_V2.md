# Taxonomy v2 (proposal)

Status: **proposal only. Nothing has been migrated. Awaiting approval before any data change.**

This document proposes 12 aisles and 87 leaf categories, replacing today's 10 top-level groups and 32 assignable categories. It covers the gaps found in the prototype (pet food, cosmetics and sun care, kitchenware, books, baby furniture, wine and cocktails, coffee/tea/infusions, chocolate, snacks), splits the catch-all categories (Mercearia, Bolachas e Biscoitos, Bebidas Alcoólicas, Bolos e Sobremesas, Higiene Pessoal), and turns Bio, sem lactose and sem glúten into tags.

## Why change

- Celeiro is 100% uncategorised, and whole aisles have no home today, so products pile up uncategorised.
- Several v1 categories are catch-alls (`bolachas`, `bebidas-alcoolicas`, `bolos-sobremesas`, `higiene-pessoal`, and products sitting directly on the `Mercearia` parent). A classifier cannot learn a coherent label from a catch-all.
- 'Bio', 'sem lactose' and 'sem glúten' cut across aisles (a bio yoghurt and a bio rice are both bio); as categories they fragment the tree and confuse the classifier. As tags they combine with any category.

## Aisles and categories

### Frutas e Legumes (`frutas-legumes`) — 3 categories

| Category | Slug | Notes |
|---|---|---|
| Frutas | `frutas` | kept from v1 `frutas` |
| Legumes e Hortícolas | `legumes` | kept from v1 `legumes` |
| Saladas e Ervas Aromáticas | `saladas-ervas` | new: bagged salads, herbs, sprouts |

### Talho e Peixaria (`talho-peixaria`) — 7 categories

| Category | Slug | Notes |
|---|---|---|
| Carne de Vaca | `carne-vaca` | split of v1 `carne` |
| Carne de Porco | `carne-porco` | split of v1 `carne` |
| Aves | `aves` | split of v1 `carne`: chicken, turkey, duck |
| Carne Picada e Preparados | `carne-picada-preparados` | split of v1 `carne`: mince, burgers, marinated, kebabs |
| Peixe Fresco | `peixe-fresco` | split of v1 `peixe-marisco` |
| Marisco | `marisco` | split of v1 `peixe-marisco` |
| Bacalhau e Salgados | `bacalhau-salgados` | new: dried/salted cod and other salted fish |

### Charcutaria e Queijos (`charcutaria-queijos`) — 4 categories

| Category | Slug | Notes |
|---|---|---|
| Fiambre e Presunto | `fiambre-presunto` | split of v1 `charcutaria` |
| Enchidos | `enchidos` | split of v1 `charcutaria`: chouriço, alheira, salsichas |
| Patés e Charcutaria Cozida | `pates-cozidos` | split of v1 `charcutaria` |
| Queijos | `queijos` | moved from Laticínios; v1 `queijos` |

### Laticínios e Ovos (`laticinios-ovos`) — 7 categories

| Category | Slug | Notes |
|---|---|---|
| Leite | `leite` | kept from v1 `leite` |
| Bebidas Vegetais | `bebidas-vegetais` | new: soy, oat, almond drinks (today mixed into leite/sumos) |
| Iogurtes | `iogurtes` | kept from v1 `iogurtes` |
| Sobremesas Lácteas | `sobremesas-lacteas` | split of v1 `iogurtes` and `bolos-sobremesas`: pudins, gelatinas, arroz doce, mousses |
| Manteiga e Margarinas | `manteiga-margarinas` | kept from v1 `manteiga-margarinas` |
| Natas e Cremes Culinários | `natas-cremes` | kept from v1 `natas-cremes` |
| Ovos | `ovos` | moved from Frescos; v1 `ovos` |

### Padaria e Pastelaria (`padaria-pastelaria`) — 4 categories

| Category | Slug | Notes |
|---|---|---|
| Pão | `pao` | kept from v1 `pao` |
| Pão de Forma e Embalado | `pao-forma-embalado` | split of v1 `pao`: sliced bread, wraps, tostas de pão |
| Bolos e Pastelaria | `bolos-pastelaria` | split of v1 `bolos-sobremesas` |
| Sobremesas Preparadas | `sobremesas-preparadas` | split of v1 `bolos-sobremesas`: tartes, tiramisu, prepared desserts (non-dairy-aisle) |

### Mercearia (`mercearia`) — 9 categories

| Category | Slug | Notes |
|---|---|---|
| Arroz | `arroz` | kept from v1 `arroz` |
| Massas | `massas` | kept from v1 `massas` |
| Leguminosas e Grãos | `leguminosas-graos` | new: beans, chickpeas, lentils, quinoa, couscous |
| Farinhas e Preparados | `farinhas-preparados` | new: flour, baking mixes, yeast, breadcrumbs |
| Conservas de Peixe | `conservas-peixe` | split of v1 `conservas`: tuna, sardines, mackerel |
| Conservas Vegetais e de Fruta | `conservas-vegetais` | split of v1 `conservas` |
| Molhos e Temperos | `molhos-temperos` | kept from v1 `molhos-temperos` |
| Azeite, Óleos e Vinagres | `azeite-oleos` | kept from v1 `azeite-oleos` (+ vinegars) |
| Sopas e Pratos Preparados | `sopas-pratos-preparados` | new: soups, ready-to-heat jars and pouches |

### Doces, Café e Snacks (`doces-cafe-snacks`) — 13 categories

| Category | Slug | Notes |
|---|---|---|
| Cereais e Granola | `cereais` | kept from v1 `cereais` |
| Bolachas Maria e Simples | `bolachas-simples` | split of v1 `bolachas` |
| Bolachas Recheadas e Wafers | `bolachas-recheadas` | split of v1 `bolachas` |
| Bolachas Integrais e de Cereais | `bolachas-integrais` | split of v1 `bolachas` |
| Bolachas Salgadas e Tostas | `bolachas-salgadas` | split of v1 `bolachas` |
| Chocolate | `chocolate` | new gap: bars, tablets, cocoa, spreads with chocolate |
| Confeitaria e Doces | `confeitaria-doces` | new: sweets, gums, candy |
| Compotas, Mel e Cremes de Barrar | `compotas-mel` | new |
| Açúcar e Adoçantes | `acucar-adocantes` | new |
| Café | `cafe` | new gap: ground, beans, capsules, soluble |
| Chá e Infusões | `cha-infusoes` | new gap |
| Snacks Salgados | `snacks-salgados` | new gap: crisps, popcorn, puffs |
| Frutos Secos e Sementes | `frutos-secos` | new |

### Bebidas (`bebidas`) — 8 categories

| Category | Slug | Notes |
|---|---|---|
| Água | `agua` | kept from v1 `agua` |
| Refrigerantes | `refrigerantes` | split of v1 `sumos` |
| Sumos e Néctares | `sumos` | kept from v1 `sumos` |
| Bebidas Energéticas e Desportivas | `energeticas-desporto` | new |
| Cerveja e Sidra | `cerveja` | split of v1 `bebidas-alcoolicas` |
| Vinho | `vinho` | split of v1 `bebidas-alcoolicas` |
| Espirituosas e Licores | `espirituosas-licores` | split of v1 `bebidas-alcoolicas` |
| Cocktails e Bebidas Mistas | `cocktails` | new gap: ready-to-drink, sangria, mixers |

### Congelados (`congelados`) — 7 categories

| Category | Slug | Notes |
|---|---|---|
| Legumes Congelados | `legumes-congelados` | kept from v1 `legumes-congelados` |
| Peixe e Marisco Congelados | `peixe-marisco-congelado` | kept from v1 `peixe-congelado` |
| Carne e Aves Congeladas | `carne-congelada` | new |
| Refeições Prontas Congeladas | `refeicoes-prontas` | kept from v1 `refeicoes-prontas` |
| Pizzas e Salgados | `pizzas-salgados` | split of v1 `refeicoes-prontas` |
| Batatas e Pré-Fritos | `batatas-pre-fritos` | new |
| Gelados | `gelados` | new |

### Higiene e Beleza (`higiene-beleza`) — 9 categories

| Category | Slug | Notes |
|---|---|---|
| Banho e Higiene Pessoal | `banho-higiene` | split of v1 `higiene-pessoal` |
| Desodorizantes | `desodorizantes` | split of v1 `higiene-pessoal` |
| Higiene Oral | `higiene-oral` | kept from v1 `higiene-oral` |
| Cabelo | `cabelo` | split of v1 `higiene-pessoal` |
| Cosmética, Rosto e Corpo | `cosmetica-rosto-corpo` | new gap |
| Proteção Solar | `protecao-solar` | new gap: sun care, after-sun |
| Higiene Íntima e Feminina | `higiene-intima` | split of v1 `higiene-pessoal` |
| Barbear e Depilação | `barbear-depilacao` | split of v1 `higiene-pessoal` |
| Papel Higiénico e Lenços | `papel-higienico-lencos` | split of v1 `higiene-pessoal`/`limpeza-lar` |

### Casa e Limpeza (`casa-limpeza`) — 8 categories

| Category | Slug | Notes |
|---|---|---|
| Detergentes de Roupa | `detergentes-roupa` | split of v1 `detergentes` |
| Detergentes de Loiça | `detergentes-loica` | split of v1 `detergentes` |
| Limpeza do Lar | `limpeza-lar` | kept from v1 `limpeza-lar` |
| Papel e Descartáveis | `papel-descartaveis` | split of v1 `limpeza-lar`: kitchen paper, napkins, bags, foil |
| Ambientadores e Inseticidas | `ambientadores-inseticidas` | split of v1 `limpeza-lar` |
| Cozinha e Mesa | `cozinha-mesa` | new gap: kitchenware, cookware, tableware, storage |
| Papelaria e Livros | `papelaria-livros` | new gap: books, stationery |
| Bazar e Sazonais | `bazar-sazonal` | new: small appliances, batteries, bulbs, seasonal |

### Bebé, Animais e Saúde (`bebe-animais-saude`) — 8 categories

| Category | Slug | Notes |
|---|---|---|
| Alimentação Bebé | `alimentacao-bebe` | split of v1 `bebe-puericultura`: milks, purées, cereals |
| Fraldas e Higiene Bebé | `fraldas-higiene-bebe` | split of v1 `bebe-puericultura` |
| Puericultura e Mobiliário Bebé | `puericultura-mobiliario` | new gap: cribs, strollers, car seats, toys |
| Comida para Cães | `comida-caes` | new gap |
| Comida para Gatos | `comida-gatos` | new gap |
| Outros Animais e Acessórios | `animais-acessorios` | new gap: birds, fish, litter, toys |
| Suplementos e Bem-Estar | `suplementos-bem-estar` | split of v1 `saude-bem-estar`: vitamins, dietetic |
| Farmácia e Primeiros Socorros | `farmacia-primeiros-socorros` | split of v1 `saude-bem-estar` |

## Tags (not categories)

| Tag | Meaning | How it is set |
|---|---|---|
| `bio` | Organic | Deterministic rule on name/brand/store category: `bio`, `biológico`, `orgânico`, `organic`. |
| `sem-lactose` | Lactose free | Rule: `sem lactose`, `lactose free`, `zero lactose`. |
| `sem-gluten` | Gluten free | Rule: `sem glúten`, `sem gluten`, `gluten free`. |

Tags are stored per canonical product (a small `ProductTags` table: product id + tag) and are set by these rules only, never by the model. They are removed from category names and never affect the category. Extra tags (`vegan`, `sem-acucar`) can be added later without touching the tree; they are not proposed now.

## Mapping from the current categories

`1:1` means every product keeps its place (only the aisle or name may change). `split` means the v1 category fans out; each product is placed by a deterministic keyword rule where one matches, and otherwise left for the classifier or the review queue. No product is guessed silently.

| Current (v1) slug | Kind | New category or categories | How products are placed / notes |
|---|---|---|---|
| `leite` | 1:1 | `leite` | Products with `sem lactose` in the name get that tag; the category does not change. Vegetable drinks are split out by rule (soja, aveia, amêndoa, arroz + `bebida`). |
| `iogurtes` | split | `iogurtes`, `sobremesas-lacteas` | By rule: pudim, gelatina, mousse, arroz doce, sobremesa -> Sobremesas Lácteas. |
| `queijos` | 1:1 | `queijos` | Moves aisle (Laticínios -> Charcutaria e Queijos); slug unchanged. |
| `manteiga-margarinas` | 1:1 | `manteiga-margarinas` |  |
| `natas-cremes` | 1:1 | `natas-cremes` | Renamed 'Natas e Cremes Culinários'. |
| `frutas` | 1:1 | `frutas` |  |
| `legumes` | split | `legumes`, `saladas-ervas` | By rule: salada, alface embalada, ervas, rebentos. |
| `carne` | split | `carne-vaca`, `carne-porco`, `aves`, `carne-picada-preparados` | By keyword (vaca/novilho/bife, porco/entremeada, frango/peru/pato, picada/hambúrguer/espetada); anything left goes to the classifier/review. |
| `peixe-marisco` | split | `peixe-fresco`, `marisco`, `bacalhau-salgados` | By keyword (camarão, mexilhão, amêijoa... -> Marisco; bacalhau, salgado -> Bacalhau e Salgados). |
| `ovos` | 1:1 | `ovos` | Moves aisle (Frescos -> Laticínios e Ovos). |
| `charcutaria` | split | `fiambre-presunto`, `enchidos`, `pates-cozidos` | By keyword (fiambre/presunto/paio-de-lombo, chouriço/alheira/salsicha/linguiça, paté/mortadela). |
| `arroz` | 1:1 | `arroz` | Arroz-doce goes to Sobremesas Lácteas by rule. |
| `massas` | 1:1 | `massas` |  |
| `conservas` | split | `conservas-peixe`, `conservas-vegetais`, `sopas-pratos-preparados` | By keyword (atum, sardinha, cavala -> peixe; grão, feijão, milho, ananás -> vegetais; sopa, guisado -> sopas). Beans/chickpeas/lentils in dry form move to Leguminosas e Grãos. |
| `molhos-temperos` | 1:1 | `molhos-temperos` | Sopas and baking mixes found here are moved by rule. |
| `azeite-oleos` | 1:1 | `azeite-oleos` | Renamed 'Azeite, Óleos e Vinagres'. |
| `cereais` | 1:1 | `cereais` |  |
| `bolachas` | split | `bolachas-simples`, `bolachas-recheadas`, `bolachas-integrais`, `bolachas-salgadas` | Too broad today. By keyword (maria, recheada/wafer/cream, integral/aveia/cereais, tostas/salgadas/crackers); the rest via classifier. |
| `pao` | split | `pao`, `pao-forma-embalado` | By keyword (fatiado, forma, wrap, tortilha -> embalado). |
| `bolos-sobremesas` | split | `bolos-pastelaria`, `sobremesas-preparadas`, `sobremesas-lacteas` | Catch-all today; split by keyword, remainder via classifier. |
| `agua` | 1:1 | `agua` |  |
| `sumos` | split | `sumos`, `refrigerantes`, `energeticas-desporto` | Refrigerantes and energy drinks are mixed in today. |
| `bebidas-alcoolicas` | split | `cerveja`, `vinho`, `espirituosas-licores`, `cocktails` | Catch-all today; by keyword (cerveja/cider, vinho/espumante/porto, whisky/vodka/gin/licor, sangria/cocktail). |
| `legumes-congelados` | 1:1 | `legumes-congelados` |  |
| `peixe-congelado` | 1:1 | `peixe-marisco-congelado` | Renamed. |
| `refeicoes-prontas` | split | `refeicoes-prontas`, `pizzas-salgados` | By keyword (pizza, croquete, rissol, folhado). |
| `higiene-pessoal` | split | `banho-higiene`, `desodorizantes`, `cabelo`, `higiene-intima`, `barbear-depilacao`, `papel-higienico-lencos`, `cosmetica-rosto-corpo`, `protecao-solar` | Catch-all today; by keyword then classifier. Sun care and cosmetics are currently uncategorised or stuffed here. |
| `higiene-oral` | 1:1 | `higiene-oral` |  |
| `detergentes` | split | `detergentes-roupa`, `detergentes-loica` | By keyword (roupa/amaciador/máquina roupa vs loiça/máquina loiça). |
| `limpeza-lar` | split | `limpeza-lar`, `papel-descartaveis`, `ambientadores-inseticidas` | By keyword (papel de cozinha, guardanapo, película, saco -> descartáveis; ambientador, inseticida -> ambientadores). |
| `bebe-puericultura` | split | `alimentacao-bebe`, `fraldas-higiene-bebe`, `puericultura-mobiliario` | By keyword (papa, leite lactantes -> alimentação; fralda, toalhita -> fraldas; berço, carrinho, cadeira -> puericultura). |
| `saude-bem-estar` | split | `suplementos-bem-estar`, `farmacia-primeiros-socorros` | By keyword (vitamina, suplemento -> suplementos; penso, desinfetante -> farmácia). |

The v1 parent groups have no products of their own in v2. Products sitting directly on a v1 parent (for example the broad `Mercearia`) count as uncategorised and go through the classifier and review queue.

New categories with no v1 source (created empty, filled by rules, the classifier and review): `bebidas-vegetais`, `leguminosas-graos`, `farinhas-preparados`, `chocolate`, `confeitaria-doces`, `compotas-mel`, `acucar-adocantes`, `cafe`, `cha-infusoes`, `snacks-salgados`, `frutos-secos`, `carne-congelada`, `batatas-pre-fritos`, `gelados`, `cozinha-mesa`, `papelaria-livros`, `bazar-sazonal`, `comida-caes`, `comida-gatos`, `animais-acessorios`.

## How the migration would work (after your approval)

1. **Additive schema only.** `ProductCategory` gets a `TaxonomyVersion` (1 or 2) and v2 categories are inserted next to v1 ones. `Product` gets `LegacyCategoryId` (the v1 id, kept forever) and, for model decisions, `CategorySource` (`legacy-map`, `rule`, `model`, `manual`), `CategoryScore`, `CategoryModel` and `CategoryDecidedAt`. A `ProductTags` table is added. Nothing is dropped or rewritten in place before the mapping runs.
2. **1:1 rows** are applied directly from the table above.
3. **Splits** are resolved by the keyword rules where they match; the rest stay uncategorised in v2 and are handled by the classifier (auto-assign at confidence >= 0.85, review queue 0.5 to 0.85). Products you categorised by hand keep the equivalent v2 category when the mapping is 1:1; for a split they are shown to you in the review queue first instead of being reassigned by a rule.
4. **Reversible.** `LegacyCategoryId` is never overwritten, so a single statement restores v1 labels; a v2 label made by a job is recognisable by `CategorySource`.
5. The existing rule-based `CategoryMapper` stays as the degraded-mode path (it is retargeted to v2 slugs); the model step only adds to it.
6. The migration is run as a dry run first: it writes a report (per v1 category: how many products map 1:1, how many are placed by rule, how many are left for the classifier) before changing any label.

## Points for you to decide

- Queijos moved to Charcutaria e Queijos (deli) and Ovos to Laticínios e Ovos: fine, or keep v1 aisles?
- Sobremesas Lácteas appears in the Laticínios aisle while other prepared desserts stay in Padaria: fine?
- `Cozinha e Mesa`, `Papelaria e Livros` and `Bazar e Sazonais` are non-food; if Savvori should stay grocery-only, these three (and the pet and baby furniture leaves) can be dropped and such products left uncategorised.
- Only three tags now; add `vegan` or `sem-acucar`?
