# Taxonomy v2 (proposal)

Status: **approved (aisles as proposed, all non-food leaves kept, tags: bio, sem-lactose, sem-gluten, vegan, sem-acucar). The migration is implemented but NOT applied to any real database: it is an explicit admin action (Admin > Taxonomy v2), with a dry-run plan first.**

Implementation notes (differences from the proposal below):
- A migrated product keeps its v1 category in `Product.LegacyCategoryId` and is marked in `Product.CategorySource` (`taxonomy-1to1`, `taxonomy-rule`, `taxonomy-left`). There is no `TaxonomyVersion` column: v1 and v2 categories are told apart by slug, and the category API shows only the active tree. v2 slugs are English, so they never collide with the Portuguese v1 slugs: v2 is a set of new rows and the v1 rows are never modified (a revert only restores product labels).
- v1 labels do not record who set them, so **hand-made v1 labels cannot be told from rule-made ones**. For a split, every product is placed by the keyword rules regardless. The dry-run plan shows the counts first, and the whole migration is reversible.
- The classifier only predicts categories that already have products. The new gap categories (pet food, coffee, chocolate, sun care, ...) start empty, so the classifier cannot fill them until they have a few examples.

This document proposes 12 aisles and 87 leaf categories, replacing today's 10 top-level groups and 32 assignable categories. It covers the gaps found in the prototype (pet food, cosmetics and sun care, kitchenware, books, baby furniture, wine and cocktails, coffee/tea/infusions, chocolate, snacks), splits the catch-all categories (Mercearia, Bolachas e Biscoitos, Bebidas Alcoólicas, Bolos e Sobremesas, Higiene Pessoal), and turns Bio, sem lactose and sem glúten into tags.

## Why change

- Celeiro is 100% uncategorised, and whole aisles have no home today, so products pile up uncategorised.
- Several v1 categories are catch-alls (`bolachas`, `bebidas-alcoolicas`, `bolos-sobremesas`, `higiene-pessoal`, and products sitting directly on the `Mercearia` parent). A classifier cannot learn a coherent label from a catch-all.
- 'Bio', 'sem lactose' and 'sem glúten' cut across aisles (a bio yoghurt and a bio rice are both bio); as categories they fragment the tree and confuse the classifier. As tags they combine with any category.


> **Slugs and languages (added after approval).** v2 slugs are English identifiers (`beef`, `dairy-eggs`); v1 slugs stay Portuguese and are hidden once v2 is applied, so the two trees never collide. Display names are per language: the default `ProductCategory.Name` is pt-PT and English names live in `ProductCategoryTranslations` (all v1 and v2 categories). The API returns the name for `?lang=` or the request's `Accept-Language` (pt or en, fallback pt); the web app forwards the browser's language. The tables below give both names and the English slug.

## Aisles and categories

### Frutas e Legumes / Fruit & Vegetables (`produce`) — 3 categories

| Category (pt-PT) | Category (en) | Slug | Notes |
|---|---|---|---|
| Frutas | Fruit | `fruit` | kept from v1 `frutas` |
| Legumes e Hortícolas | Vegetables | `vegetables` | kept from v1 `legumes` |
| Saladas e Ervas Aromáticas | Salads & Herbs | `salads-herbs` | new: bagged salads, herbs, sprouts |

### Talho e Peixaria / Meat & Fish (`meat-fish`) — 7 categories

| Category (pt-PT) | Category (en) | Slug | Notes |
|---|---|---|---|
| Carne de Vaca | Beef | `beef` | split of v1 `carne` |
| Carne de Porco | Pork | `pork` | split of v1 `carne` |
| Aves | Poultry | `poultry` | split of v1 `carne`: chicken, turkey, duck |
| Carne Picada e Preparados | Minced & Prepared Meat | `minced-prepared-meat` | split of v1 `carne`: mince, burgers, marinated, kebabs |
| Peixe Fresco | Fresh Fish | `fresh-fish` | split of v1 `peixe-marisco` |
| Marisco | Seafood | `seafood` | split of v1 `peixe-marisco` |
| Bacalhau e Salgados | Salt Cod & Cured Fish | `salt-cod-cured-fish` | new: dried/salted cod and other salted fish |

### Charcutaria e Queijos / Deli & Cheese (`deli-cheese`) — 4 categories

| Category (pt-PT) | Category (en) | Slug | Notes |
|---|---|---|---|
| Fiambre e Presunto | Ham & Cold Cuts | `ham-cold-cuts` | split of v1 `charcutaria` |
| Enchidos | Cured Sausages | `cured-sausages` | split of v1 `charcutaria`: chouriço, alheira, salsichas |
| Patés e Charcutaria Cozida | Pâtés & Cooked Deli | `pates-cooked-deli` | split of v1 `charcutaria` |
| Queijos | Cheese | `cheese` | moved from Laticínios; v1 `queijos` |

### Laticínios e Ovos / Dairy & Eggs (`dairy-eggs`) — 7 categories

| Category (pt-PT) | Category (en) | Slug | Notes |
|---|---|---|---|
| Leite | Milk | `milk` | kept from v1 `leite` |
| Bebidas Vegetais | Plant-Based Drinks | `plant-drinks` | new: soy, oat, almond drinks (today mixed into leite/sumos) |
| Iogurtes | Yoghurt | `yoghurt` | kept from v1 `iogurtes` |
| Sobremesas Lácteas | Dairy Desserts | `dairy-desserts` | split of v1 `iogurtes` and `bolos-sobremesas`: pudins, gelatinas, arroz doce, mousses |
| Manteiga e Margarinas | Butter & Margarine | `butter-margarine` | kept from v1 `manteiga-margarinas` |
| Natas e Cremes Culinários | Cooking Cream | `cooking-cream` | kept from v1 `natas-cremes` |
| Ovos | Eggs | `eggs` | moved from Frescos; v1 `ovos` |

### Padaria e Pastelaria / Bakery & Pastry (`bakery`) — 4 categories

| Category (pt-PT) | Category (en) | Slug | Notes |
|---|---|---|---|
| Pão | Bread | `bread` | kept from v1 `pao` |
| Pão de Forma e Embalado | Sliced & Packaged Bread | `packaged-bread` | split of v1 `pao`: sliced bread, wraps, tostas de pão |
| Bolos e Pastelaria | Cakes & Pastries | `cakes-pastries` | split of v1 `bolos-sobremesas` |
| Sobremesas Preparadas | Prepared Desserts | `prepared-desserts` | split of v1 `bolos-sobremesas`: tartes, tiramisu, prepared desserts (non-dairy-aisle) |

### Mercearia / Pantry (`pantry`) — 9 categories

| Category (pt-PT) | Category (en) | Slug | Notes |
|---|---|---|---|
| Arroz | Rice | `rice` | kept from v1 `arroz` |
| Massas | Pasta | `pasta` | kept from v1 `massas` |
| Leguminosas e Grãos | Pulses & Grains | `pulses-grains` | new: beans, chickpeas, lentils, quinoa, couscous |
| Farinhas e Preparados | Flour & Baking | `flour-baking` | new: flour, baking mixes, yeast, breadcrumbs |
| Conservas de Peixe | Canned Fish | `canned-fish` | split of v1 `conservas`: tuna, sardines, mackerel |
| Conservas Vegetais e de Fruta | Canned Vegetables & Fruit | `canned-vegetables-fruit` | split of v1 `conservas` |
| Molhos e Temperos | Sauces & Seasonings | `sauces-seasonings` | kept from v1 `molhos-temperos` |
| Azeite, Óleos e Vinagres | Olive Oil, Oils & Vinegar | `oil-vinegar` | kept from v1 `azeite-oleos` (+ vinegars) |
| Sopas e Pratos Preparados | Soups & Ready Meals | `soups-ready-meals` | new: soups, ready-to-heat jars and pouches |

### Doces, Café e Snacks / Sweets, Coffee & Snacks (`sweets-coffee-snacks`) — 13 categories

| Category (pt-PT) | Category (en) | Slug | Notes |
|---|---|---|---|
| Cereais e Granola | Cereals & Granola | `cereals` | kept from v1 `cereais` |
| Bolachas Maria e Simples | Plain Biscuits | `plain-biscuits` | split of v1 `bolachas` |
| Bolachas Recheadas e Wafers | Filled Biscuits & Wafers | `filled-biscuits` | split of v1 `bolachas` |
| Bolachas Integrais e de Cereais | Wholegrain & Cereal Biscuits | `wholegrain-biscuits` | split of v1 `bolachas` |
| Bolachas Salgadas e Tostas | Crackers & Savoury Biscuits | `crackers` | split of v1 `bolachas` |
| Chocolate | Chocolate | `chocolate` | new gap: bars, tablets, cocoa, spreads with chocolate |
| Confeitaria e Doces | Confectionery & Sweets | `confectionery` | new: sweets, gums, candy |
| Compotas, Mel e Cremes de Barrar | Jams, Honey & Spreads | `jams-honey-spreads` | new |
| Açúcar e Adoçantes | Sugar & Sweeteners | `sugar-sweeteners` | new |
| Café | Coffee | `coffee` | new gap: ground, beans, capsules, soluble |
| Chá e Infusões | Tea & Infusions | `tea-infusions` | new gap |
| Snacks Salgados | Savoury Snacks | `savoury-snacks` | new gap: crisps, popcorn, puffs |
| Frutos Secos e Sementes | Nuts & Seeds | `nuts-seeds` | new |

### Bebidas / Drinks (`drinks`) — 8 categories

| Category (pt-PT) | Category (en) | Slug | Notes |
|---|---|---|---|
| Água | Water | `water` | kept from v1 `agua` |
| Refrigerantes | Soft Drinks | `soft-drinks` | split of v1 `sumos` |
| Sumos e Néctares | Juices & Nectars | `juices` | kept from v1 `sumos` |
| Bebidas Energéticas e Desportivas | Energy & Sports Drinks | `energy-sports-drinks` | new |
| Cerveja e Sidra | Beer & Cider | `beer-cider` | split of v1 `bebidas-alcoolicas` |
| Vinho | Wine | `wine` | split of v1 `bebidas-alcoolicas` |
| Espirituosas e Licores | Spirits & Liqueurs | `spirits-liqueurs` | split of v1 `bebidas-alcoolicas` |
| Cocktails e Bebidas Mistas | Cocktails & Mixed Drinks | `cocktails-mixed` | new gap: ready-to-drink, sangria, mixers |

### Congelados / Frozen (`frozen`) — 7 categories

| Category (pt-PT) | Category (en) | Slug | Notes |
|---|---|---|---|
| Legumes Congelados | Frozen Vegetables | `frozen-vegetables` | kept from v1 `legumes-congelados` |
| Peixe e Marisco Congelados | Frozen Fish & Seafood | `frozen-fish-seafood` | kept from v1 `peixe-congelado` |
| Carne e Aves Congeladas | Frozen Meat & Poultry | `frozen-meat-poultry` | new |
| Refeições Prontas Congeladas | Frozen Ready Meals | `frozen-ready-meals` | kept from v1 `refeicoes-prontas` |
| Pizzas e Salgados | Pizzas & Savouries | `pizzas-savouries` | split of v1 `refeicoes-prontas` |
| Batatas e Pré-Fritos | Potatoes & Fries | `potatoes-fries` | new |
| Gelados | Ice Cream | `ice-cream` | new |

### Higiene e Beleza / Personal Care & Beauty (`personal-care`) — 9 categories

| Category (pt-PT) | Category (en) | Slug | Notes |
|---|---|---|---|
| Banho e Higiene Pessoal | Bath & Body | `bath-body` | split of v1 `higiene-pessoal` |
| Desodorizantes | Deodorants | `deodorants` | split of v1 `higiene-pessoal` |
| Higiene Oral | Oral Care | `oral-care` | kept from v1 `higiene-oral` |
| Cabelo | Hair Care | `hair-care` | split of v1 `higiene-pessoal` |
| Cosmética, Rosto e Corpo | Skincare & Cosmetics | `skincare-cosmetics` | new gap |
| Proteção Solar | Sun Care | `sun-care` | new gap: sun care, after-sun |
| Higiene Íntima e Feminina | Intimate & Feminine Care | `intimate-care` | split of v1 `higiene-pessoal` |
| Barbear e Depilação | Shaving & Hair Removal | `shaving-hair-removal` | split of v1 `higiene-pessoal` |
| Papel Higiénico e Lenços | Toilet Paper & Tissues | `toilet-paper-tissues` | split of v1 `higiene-pessoal`/`limpeza-lar` |

### Casa e Limpeza / Home & Cleaning (`home-cleaning`) — 8 categories

| Category (pt-PT) | Category (en) | Slug | Notes |
|---|---|---|---|
| Detergentes de Roupa | Laundry | `laundry` | split of v1 `detergentes` |
| Detergentes de Loiça | Dishwashing | `dishwashing` | split of v1 `detergentes` |
| Limpeza do Lar | Household Cleaning | `household-cleaning` | kept from v1 `limpeza-lar` |
| Papel e Descartáveis | Paper & Disposables | `paper-disposables` | split of v1 `limpeza-lar`: kitchen paper, napkins, bags, foil |
| Ambientadores e Inseticidas | Air Fresheners & Insecticides | `air-fresheners-insecticides` | split of v1 `limpeza-lar` |
| Cozinha e Mesa | Kitchen & Dining | `kitchen-dining` | new gap: kitchenware, cookware, tableware, storage |
| Papelaria e Livros | Stationery & Books | `stationery-books` | new gap: books, stationery |
| Bazar e Sazonais | General & Seasonal | `general-seasonal` | new: small appliances, batteries, bulbs, seasonal |

### Bebé, Animais e Saúde / Baby, Pets & Health (`baby-pets-health`) — 8 categories

| Category (pt-PT) | Category (en) | Slug | Notes |
|---|---|---|---|
| Alimentação Bebé | Baby Food | `baby-food` | split of v1 `bebe-puericultura`: milks, purées, cereals |
| Fraldas e Higiene Bebé | Nappies & Baby Care | `nappies-baby-care` | split of v1 `bebe-puericultura` |
| Puericultura e Mobiliário Bebé | Baby Gear & Furniture | `baby-gear-furniture` | new gap: cribs, strollers, car seats, toys |
| Comida para Cães | Dog Food | `dog-food` | new gap |
| Comida para Gatos | Cat Food | `cat-food` | new gap |
| Outros Animais e Acessórios | Other Pets & Supplies | `pet-supplies` | new gap: birds, fish, litter, toys |
| Suplementos e Bem-Estar | Supplements & Wellness | `supplements-wellness` | split of v1 `saude-bem-estar`: vitamins, dietetic |
| Farmácia e Primeiros Socorros | Pharmacy & First Aid | `pharmacy-first-aid` | split of v1 `saude-bem-estar` |

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
| `leite` | 1:1 | `milk` | Products with `sem lactose` in the name get that tag; the category does not change. Vegetable drinks are split out by rule (soja, aveia, amêndoa, arroz + `bebida`). |
| `iogurtes` | split | `yoghurt`, `dairy-desserts` | By rule: pudim, gelatina, mousse, arroz doce, sobremesa -> Sobremesas Lácteas. |
| `queijos` | 1:1 | `cheese` | Moves aisle (Laticínios -> Charcutaria e Queijos); slug unchanged. |
| `manteiga-margarinas` | 1:1 | `butter-margarine` |  |
| `natas-cremes` | 1:1 | `cooking-cream` | Renamed 'Natas e Cremes Culinários'. |
| `frutas` | 1:1 | `fruit` |  |
| `legumes` | split | `vegetables`, `salads-herbs` | By rule: salada, alface embalada, ervas, rebentos. |
| `carne` | split | `beef`, `pork`, `poultry`, `minced-prepared-meat` | By keyword (vaca/novilho/bife, porco/entremeada, frango/peru/pato, picada/hambúrguer/espetada); anything left goes to the classifier/review. |
| `peixe-marisco` | split | `fresh-fish`, `seafood`, `salt-cod-cured-fish` | By keyword (camarão, mexilhão, amêijoa... -> Marisco; bacalhau, salgado -> Bacalhau e Salgados). |
| `ovos` | 1:1 | `eggs` | Moves aisle (Frescos -> Laticínios e Ovos). |
| `charcutaria` | split | `ham-cold-cuts`, `cured-sausages`, `pates-cooked-deli` | By keyword (fiambre/presunto/paio-de-lombo, chouriço/alheira/salsicha/linguiça, paté/mortadela). |
| `arroz` | 1:1 | `rice` | Arroz-doce goes to Sobremesas Lácteas by rule. |
| `massas` | 1:1 | `pasta` |  |
| `conservas` | split | `canned-fish`, `canned-vegetables-fruit`, `soups-ready-meals` | By keyword (atum, sardinha, cavala -> peixe; grão, feijão, milho, ananás -> vegetais; sopa, guisado -> sopas). Beans/chickpeas/lentils in dry form move to Leguminosas e Grãos. |
| `molhos-temperos` | 1:1 | `sauces-seasonings` | Sopas and baking mixes found here are moved by rule. |
| `azeite-oleos` | 1:1 | `oil-vinegar` | Renamed 'Azeite, Óleos e Vinagres'. |
| `cereais` | 1:1 | `cereals` |  |
| `bolachas` | split | `plain-biscuits`, `filled-biscuits`, `wholegrain-biscuits`, `crackers` | Too broad today. By keyword (maria, recheada/wafer/cream, integral/aveia/cereais, tostas/salgadas/crackers); the rest via classifier. |
| `pao` | split | `bread`, `packaged-bread` | By keyword (fatiado, forma, wrap, tortilha -> embalado). |
| `bolos-sobremesas` | split | `cakes-pastries`, `prepared-desserts`, `dairy-desserts` | Catch-all today; split by keyword, remainder via classifier. |
| `agua` | 1:1 | `water` |  |
| `sumos` | split | `juices`, `soft-drinks`, `energy-sports-drinks` | Refrigerantes and energy drinks are mixed in today. |
| `bebidas-alcoolicas` | split | `beer-cider`, `wine`, `spirits-liqueurs`, `cocktails-mixed` | Catch-all today; by keyword (cerveja/cider, vinho/espumante/porto, whisky/vodka/gin/licor, sangria/cocktail). |
| `legumes-congelados` | 1:1 | `frozen-vegetables` |  |
| `peixe-congelado` | 1:1 | `frozen-fish-seafood` | Renamed. |
| `refeicoes-prontas` | split | `frozen-ready-meals`, `pizzas-savouries` | By keyword (pizza, croquete, rissol, folhado). |
| `higiene-pessoal` | split | `bath-body`, `deodorants`, `hair-care`, `intimate-care`, `shaving-hair-removal`, `toilet-paper-tissues`, `skincare-cosmetics`, `sun-care` | Catch-all today; by keyword then classifier. Sun care and cosmetics are currently uncategorised or stuffed here. |
| `higiene-oral` | 1:1 | `oral-care` |  |
| `detergentes` | split | `laundry`, `dishwashing` | By keyword (roupa/amaciador/máquina roupa vs loiça/máquina loiça). |
| `limpeza-lar` | split | `household-cleaning`, `paper-disposables`, `air-fresheners-insecticides` | By keyword (papel de cozinha, guardanapo, película, saco -> descartáveis; ambientador, inseticida -> ambientadores). |
| `bebe-puericultura` | split | `baby-food`, `nappies-baby-care`, `baby-gear-furniture` | By keyword (papa, leite lactantes -> alimentação; fralda, toalhita -> fraldas; berço, carrinho, cadeira -> puericultura). |
| `saude-bem-estar` | split | `supplements-wellness`, `pharmacy-first-aid` | By keyword (vitamina, suplemento -> suplementos; penso, desinfetante -> farmácia). |

The v1 parent groups have no products of their own in v2. Products sitting directly on a v1 parent (for example the broad `Mercearia`) count as uncategorised and go through the classifier and review queue.

New categories with no v1 source (created empty, filled by rules, the classifier and review): `plant-drinks`, `pulses-grains`, `flour-baking`, `chocolate`, `confectionery`, `jams-honey-spreads`, `sugar-sweeteners`, `coffee`, `tea-infusions`, `savoury-snacks`, `nuts-seeds`, `frozen-meat-poultry`, `potatoes-fries`, `ice-cream`, `kitchen-dining`, `stationery-books`, `general-seasonal`, `dog-food`, `cat-food`, `pet-supplies`.

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
