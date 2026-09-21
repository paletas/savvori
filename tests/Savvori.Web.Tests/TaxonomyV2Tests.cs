using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Savvori.Shared;
using Savvori.WebApi;
using Savvori.WebApi.Scraping;

namespace Savvori.Web.Tests;

public sealed class TaxonomyV2DataTests
{
    [Fact]
    public void Taxonomy_HasTheApprovedShape_12Aisles_87Categories_UniqueSlugs()
    {
        Assert.Equal(12, TaxonomyV2Data.Aisles.Count);
        var leaves = TaxonomyV2Data.Aisles.SelectMany(a => a.Leaves).ToList();
        Assert.Equal(87, leaves.Count);
        var all = TaxonomyV2Data.Aisles.Select(a => a.Slug).Concat(leaves.Select(l => l.Slug)).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public void EveryV1Category_HasAMapping_AndEveryTargetExistsInV2()
    {
        var v1Leaves = CategoryTaxonomy.All.Where(d => d.ParentSlug is not null || d.Slug is "bebe-puericultura" or "saude-bem-estar").Select(d => d.Slug);
        Assert.All(v1Leaves, slug => Assert.True(TaxonomyV2.Legacy.ContainsKey(slug), $"no mapping for v1 '{slug}'"));

        var leafSlugs = TaxonomyV2Data.Aisles.SelectMany(a => a.Leaves.Select(l => l.Slug)).ToHashSet();
        foreach (var (old, mapping) in TaxonomyV2.Legacy)
        {
            if (mapping.Default is { } d) Assert.Contains(d, leafSlugs);
            foreach (var rule in mapping.Rules) Assert.Contains(rule.Target, leafSlugs);
        }
    }

    [Fact]
    public void ThePrototypesGapCategories_ExistInV2()
    {
        var leaves = TaxonomyV2Data.Aisles.SelectMany(a => a.Leaves.Select(l => l.Slug)).ToHashSet();
        foreach (var gap in new[] { "comida-caes", "comida-gatos", "protecao-solar", "cosmetica-rosto-corpo", "cozinha-mesa",
                     "papelaria-livros", "puericultura-mobiliario", "vinho", "cocktails", "cafe", "cha-infusoes", "chocolate", "snacks-salgados" })
            Assert.Contains(gap, leaves);
    }

    [Theory]
    [InlineData("carne", "Bife de Vaca Maturada 500g", "carne-vaca")]
    [InlineData("carne", "Hambúrguer de Vaca 2x100g", "carne-picada-preparados")] // preparados beat vaca
    [InlineData("carne", "Peito de Frango Fatiado", "aves")]
    [InlineData("carne", "Entremeada de Porco", "carne-porco")]
    [InlineData("peixe-marisco", "Camarão Cozido 200g", "marisco")]
    [InlineData("peixe-marisco", "Bacalhau Graúdo", "bacalhau-salgados")]
    [InlineData("charcutaria", "Presunto Fatiado", "fiambre-presunto")]
    [InlineData("charcutaria", "Paio de Lombo", "fiambre-presunto")]
    [InlineData("charcutaria", "Chouriço de Carne", "enchidos")]
    [InlineData("conservas", "Atum em Azeite 3x80g", "conservas-peixe")]
    [InlineData("conservas", "Grão-de-bico Cozido", "conservas-vegetais")]
    [InlineData("bolachas", "Bolacha Maria Dourada", "bolachas-simples")]
    [InlineData("bolachas", "Bolacha Recheada Chocolate", "bolachas-recheadas")]
    [InlineData("bolachas", "Tostas Integrais", "bolachas-salgadas")]
    [InlineData("bebidas-alcoolicas", "Vinho Tinto Alentejo 75cl", "vinho")]
    [InlineData("bebidas-alcoolicas", "Cerveja Sagres 6x33cl", "cerveja")]
    [InlineData("bebidas-alcoolicas", "Whisky Escocês", "espirituosas-licores")]
    [InlineData("bebidas-alcoolicas", "Sangria Tinta", "cocktails")]
    [InlineData("sumos", "Coca-Cola Zero 1.5L", "refrigerantes")]
    [InlineData("sumos", "Red Bull 250ml", "energeticas-desporto")]
    [InlineData("sumos", "Sumo de Laranja 1L", "sumos")]                         // default: stays
    [InlineData("iogurtes", "Pudim de Baunilha", "sobremesas-lacteas")]
    [InlineData("iogurtes", "Iogurte Grego Natural", "iogurtes")]                 // default: stays
    [InlineData("higiene-pessoal", "Protetor Solar SPF 50", "protecao-solar")]
    [InlineData("higiene-pessoal", "Champô Anticaspa", "cabelo")]
    [InlineData("higiene-pessoal", "Desodorizante Roll On", "desodorizantes")]
    [InlineData("higiene-pessoal", "Gel de Banho Hidratante", "banho-higiene")]
    [InlineData("detergentes", "Detergente Loiça Fairy", "detergentes-loica")]
    [InlineData("detergentes", "Amaciador Roupa", "detergentes-roupa")]
    [InlineData("limpeza-lar", "Papel de Cozinha 2 rolos", "papel-descartaveis")]
    [InlineData("bebe-puericultura", "Fraldas Pampers T3", "fraldas-higiene-bebe")]
    [InlineData("bebe-puericultura", "Papa Láctea Nutriben", "alimentacao-bebe")]
    [InlineData("pao", "Pão de Forma Fatiado", "pao-forma-embalado")]
    [InlineData("pao", "Pão Alentejano", "pao")]                                  // default: stays
    [InlineData("leite", "Bebida de Aveia 1L", "bebidas-vegetais")]
    [InlineData("leite", "Leite Meio Gordo 1L", "leite")]
    public void Place_UsesKeywordRules_ThenTheDefault(string legacy, string name, string expected)
    {
        var placement = TaxonomyV2.Place(legacy, name)!;
        Assert.Equal(expected, placement.TargetSlug);
    }

    [Theory]
    [InlineData("carne", "Carne Qualquer Coisa")]
    [InlineData("higiene-pessoal", "Produto Misterioso")]
    [InlineData("bolachas", "Bolacha Desconhecida XPTO")]
    public void UnmatchedSplits_AreLeftForTheClassifier_NeverGuessed(string legacy, string name)
    {
        var placement = TaxonomyV2.Place(legacy, name)!;
        Assert.Null(placement.TargetSlug);
        Assert.Equal(PlacementKind.LeftForClassifier, placement.Kind);
    }

    [Fact]
    public void KeywordsMatchWholeWords_NotSubstrings()
    {
        // "gin" must not fire inside "original", "rum" not inside "drummer"
        Assert.Null(TaxonomyV2.Place("bebidas-alcoolicas", "Original Drummer Special")!.TargetSlug);
    }

    [Theory]
    [InlineData("Leite Sem Lactose Mimosa 1L", null, null, "sem-lactose")]
    [InlineData("Massa Sem Glúten 500g", null, null, "sem-gluten")]
    [InlineData("Arroz Carolino", "Pingo Doce Bio", null, "bio")]
    [InlineData("Bolachas Integrais Biológicas", null, null, "bio")]
    [InlineData("Hambúrguer Vegan 2x", null, null, "vegan")]
    [InlineData("Compota Sem Açúcar", null, null, "sem-acucar")]
    [InlineData("Iogurte", null, "produtos-lacteos/bio", "bio")]
    public void TagRules_DetectTagsFromNameBrandOrStoreCategory(string name, string? brand, string? raw, string expected) =>
        Assert.Contains(expected, TagRules.Compute(name, brand, raw));

    [Theory]
    [InlineData("Biomassa Energética")]
    [InlineData("Leite Mimosa")]
    [InlineData("Arroz Agulha")]
    public void TagRules_DoNotFireOnUnrelatedWords(string name) => Assert.Empty(TagRules.Compute(name, null, null));

    [Fact]
    public void TagRules_CanCombine() =>
        Assert.Equal(new[] { "bio", "sem-lactose", "sem-gluten" }.Order(),
            TagRules.Compute("Bebida Bio Sem Lactose Sem Glúten", null, null).Order());
}

public sealed class TaxonomyMigrationTests : IAsyncLifetime
{
    private SavvoriDbContext _db = default!;
    private TaxonomyMigrationService _svc = default!;
    private readonly TimeProvider _time = TimeProvider.System;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _db = new SavvoriDbContext(new DbContextOptionsBuilder<SavvoriDbContext>()
            .UseInMemoryDatabase($"Taxonomy_{Guid.NewGuid()}").Options);
        await CategorySeeder.SeedAsync(_db, ct: Ct); // v1 taxonomy, as on a real database
        _svc = new TaxonomyMigrationService(_db, _time);
    }

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();

    private Guid V1(string slug) => _db.ProductCategories.Single(c => c.Slug == slug).Id;

    private async Task<Guid> AddProduct(string name, string? v1Slug, string? brand = null, string? raw = null)
    {
        var p = new Product { Id = Guid.NewGuid(), Name = name, Brand = brand, Category = raw, CategoryId = v1Slug is null ? null : V1(v1Slug) };
        _db.Products.Add(p);
        await _db.SaveChangesAsync(Ct);
        return p.Id;
    }

    private Task<Product> Get(Guid id) => _db.Products.AsNoTracking().SingleAsync(p => p.Id == id, Ct);
    private Task<string?> SlugOf(Guid? id) => id is null ? Task.FromResult<string?>(null)
        : _db.ProductCategories.Where(c => c.Id == id).Select(c => c.Slug).SingleAsync(Ct)!;

    [Fact]
    public async Task Plan_IsADryRun_ItChangesNothing_AndCountsPerV1Category()
    {
        await AddProduct("Bife de Vaca", "carne");
        await AddProduct("Frango Inteiro", "carne");
        await AddProduct("Carne Sem Palavra Chave", "carne");
        await AddProduct("Leite Meio Gordo", "leite");
        var before = await _db.Products.CountAsync(Ct);

        var plan = await _svc.PlanAsync(Ct);

        Assert.False(plan.V2Active);
        var carne = plan.Rows.Single(r => r.LegacySlug == "carne");
        Assert.Equal(3, carne.Products);
        Assert.Equal(2, carne.ByRule);
        Assert.Equal(1, carne.LeftForClassifier);
        Assert.Equal(1, carne.RuleTargets["carne-vaca"]);
        Assert.Equal(1, plan.Rows.Single(r => r.LegacySlug == "leite").OneToOne);
        Assert.Equal(before, await _db.Products.CountAsync(Ct));
        Assert.Equal(0, await _db.Products.CountAsync(p => p.LegacyCategoryId != null, Ct));
        Assert.False(await _db.ProductCategories.AnyAsync(c => c.Slug == "carne-vaca", Ct));
    }

    [Fact]
    public async Task Apply_SeedsTheV2Tree_ReusingSharedSlugsAndReparentingThem()
    {
        var leiteIdBefore = V1("leite");

        var result = await _svc.ApplyAsync(Ct);

        Assert.True(result.Applied);
        Assert.Equal(12, await _db.ProductCategories.CountAsync(c => TaxonomyV2.AisleSlugs.Contains(c.Slug), Ct));
        var leaves = await _db.ProductCategories.CountAsync(c => TaxonomyV2.V2Slugs.Contains(c.Slug) && !TaxonomyV2.AisleSlugs.Contains(c.Slug), Ct);
        Assert.Equal(87, leaves);
        var leite = await _db.ProductCategories.Include(c => c.Parent).SingleAsync(c => c.Slug == "leite", Ct);
        Assert.Equal(leiteIdBefore, leite.Id);                 // same row, same id
        Assert.Equal("laticinios-ovos", leite.Parent!.Slug);   // now in the new aisle
        Assert.True(await _svc.IsV2ActiveAsync(Ct));
    }

    [Fact]
    public async Task Apply_RelabelsProducts_KeepingTheV1Category_AndMarkingTheSource()
    {
        var vaca = await AddProduct("Bife de Vaca", "carne");
        var unknown = await AddProduct("Carne Sem Palavra Chave", "carne");
        var leite = await AddProduct("Leite Meio Gordo", "leite");
        var soja = await AddProduct("Bebida de Soja", "leite");
        var carneV1 = V1("carne");

        await _svc.ApplyAsync(Ct);

        var pVaca = await Get(vaca);
        Assert.Equal("carne-vaca", await SlugOf(pVaca.CategoryId));
        Assert.Equal(carneV1, pVaca.LegacyCategoryId);
        Assert.Equal("taxonomy-rule", pVaca.CategorySource);

        var pUnknown = await Get(unknown);
        Assert.Null(pUnknown.CategoryId);                       // left for the classifier, not guessed
        Assert.Equal(carneV1, pUnknown.LegacyCategoryId);
        Assert.Equal("taxonomy-left", pUnknown.CategorySource);

        var pLeite = await Get(leite);
        Assert.Equal("leite", await SlugOf(pLeite.CategoryId));
        Assert.Equal("taxonomy-1to1", pLeite.CategorySource);

        Assert.Equal("bebidas-vegetais", await SlugOf((await Get(soja)).CategoryId));
    }

    [Fact]
    public async Task ProductsOnAV1ParentGroup_BecomeUncategorised_AndUncategorisedOnesAreUntouched()
    {
        var onParent = await AddProduct("Coisa qualquer", "mercearia");
        var none = await AddProduct("Sem categoria", null);

        await _svc.ApplyAsync(Ct);

        Assert.Null((await Get(onParent)).CategoryId);
        Assert.NotNull((await Get(onParent)).LegacyCategoryId);
        var untouched = await Get(none);
        Assert.Null(untouched.CategoryId);
        Assert.Null(untouched.LegacyCategoryId);
        Assert.Null(untouched.CategorySource);
    }

    [Fact]
    public async Task Apply_SetsTags_AndIsRefusedASecondTime()
    {
        var p = await AddProduct("Leite Sem Lactose Bio", "leite");

        var first = await _svc.ApplyAsync(Ct);
        var second = await _svc.ApplyAsync(Ct);

        var tags = await _db.ProductTags.Where(t => t.ProductId == p).Select(t => t.Tag).ToListAsync(Ct);
        Assert.Contains("bio", tags);
        Assert.Contains("sem-lactose", tags);
        Assert.True(first.Applied);
        Assert.False(second.Applied);
        Assert.Single(await _db.TaxonomyMigrations.ToListAsync(Ct));
    }

    [Fact]
    public async Task BackfillTags_IsIdempotent()
    {
        await AddProduct("Arroz Bio", "arroz");
        var first = await _svc.BackfillTagsAsync(Ct);
        var second = await _svc.BackfillTagsAsync(Ct);

        Assert.Equal(1, first);
        Assert.Equal(0, second);
    }

    [Fact]
    public async Task Revert_RestoresEveryMigratedProduct_AndTheV1Tree()
    {
        var vaca = await AddProduct("Bife de Vaca", "carne");
        var unknown = await AddProduct("Carne Sem Palavra Chave", "carne");
        var leite = await AddProduct("Leite", "leite");
        var carneV1 = V1("carne");
        var leiteRow = await _db.ProductCategories.AsNoTracking().SingleAsync(c => c.Slug == "leite", Ct);
        await _svc.ApplyAsync(Ct);

        var (reverted, _, restored) = await _svc.RevertAsync(Ct);

        Assert.True(reverted);
        Assert.Equal(3, restored);
        foreach (var id in new[] { vaca, unknown })
        {
            var p = await Get(id);
            Assert.Equal(carneV1, p.CategoryId);
            Assert.Null(p.LegacyCategoryId);
            Assert.Null(p.CategorySource);
        }
        Assert.Equal(V1("leite"), (await Get(leite)).CategoryId);
        var leiteAfter = await _db.ProductCategories.Include(c => c.Parent).AsNoTracking().SingleAsync(c => c.Slug == "leite", Ct);
        Assert.Equal(leiteRow.Name, leiteAfter.Name);
        Assert.Equal("laticinios", leiteAfter.Parent!.Slug);   // back under its v1 parent
        Assert.False(await _svc.IsV2ActiveAsync(Ct));
        Assert.False((await _svc.RevertAsync(Ct)).Reverted);   // nothing left to revert
    }

    [Fact]
    public async Task Revert_NeverUndoesAHandDecision_AndDropsLabelsSetOnTheV2TreeSinceThen()
    {
        var byHand = await AddProduct("Bife de Vaca", "carne");
        var byClassifier = await AddProduct("Produto sem categoria", null);
        await _svc.ApplyAsync(Ct);

        // You re-categorise by hand (the endpoint clears the marker); the classifier labels another product in v2.
        var hand = await _db.Products.SingleAsync(p => p.Id == byHand, Ct);
        hand.CategoryId = _db.ProductCategories.Single(c => c.Slug == "carne-porco").Id;
        hand.CategorySource = null;
        var auto = await _db.Products.SingleAsync(p => p.Id == byClassifier, Ct);
        auto.CategoryId = _db.ProductCategories.Single(c => c.Slug == "cafe").Id;
        _db.CategorySuggestions.Add(new CategorySuggestion
        {
            Id = Guid.NewGuid(), ProductId = byClassifier, SuggestedCategoryId = auto.CategoryId!.Value, Confidence = 0.9,
            Status = CategorySuggestionStatus.Applied, Method = "embedding-knn", ModelName = "m", ModelDigest = "d", CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(Ct);

        await _svc.RevertAsync(Ct);

        Assert.Equal("carne-porco", await SlugOf((await Get(byHand)).CategoryId)); // hand decision kept
        Assert.Null((await Get(byClassifier)).CategoryId);                         // no v1 equivalent: uncategorised
    }
}

public sealed class TaxonomyScraperTests : IAsyncLifetime
{
    private SavvoriDbContext _db = default!;
    private ScraperResultProcessor _processor = default!;
    private static readonly Guid ChainId = Guid.NewGuid();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _db = new SavvoriDbContext(new DbContextOptionsBuilder<SavvoriDbContext>()
            .UseInMemoryDatabase($"TaxScrape_{Guid.NewGuid()}").Options);
        _db.StoreChains.Add(new StoreChain { Id = ChainId, Name = "Continente", Slug = "continente", BaseUrl = "https://c.test", IsActive = true });
        await _db.SaveChangesAsync(Ct);
        await CategorySeeder.SeedAsync(_db, ct: Ct);
        _processor = new ScraperResultProcessor(_db, NullLogger<ScraperResultProcessor>.Instance);
    }

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();

    private static ScrapedProduct Scraped(string name, string category, string? brand = "Marca") =>
        new(name, brand, category, 1m, null, null, Guid.NewGuid().ToString(), null, "https://x.test", false, null, ProductUnit.Unit, 1m);

    private async Task<string?> CategorySlugOf(string productName)
    {
        var p = await _db.Products.AsNoTracking().SingleAsync(x => x.Name == productName, Ct);
        return p.CategoryId is null ? null : await _db.ProductCategories.Where(c => c.Id == p.CategoryId).Select(c => c.Slug).SingleAsync(Ct);
    }

    [Fact]
    public async Task BeforeTheMigration_ScrapingStillUsesTheV1Categories()
    {
        await _processor.ProcessProductsAsync("continente", [Scraped("Bife de Vaca", "carne")], ct: Ct);

        Assert.Equal("carne", await CategorySlugOf("Bife de Vaca"));
    }

    [Fact]
    public async Task AfterTheMigration_NewProductsGetV2Categories_ViaTheApprovedMapping()
    {
        await new TaxonomyMigrationService(_db, TimeProvider.System).ApplyAsync(Ct);

        await _processor.ProcessProductsAsync("continente",
        [
            Scraped("Bife de Vaca", "carne"),
            Scraped("Carne Sem Palavra Chave", "carne"),
            Scraped("Leite Meio Gordo", "leite"),
            Scraped("Leite Sem Lactose Bio", "leite"),
        ], ct: Ct);

        Assert.Equal("carne-vaca", await CategorySlugOf("Bife de Vaca"));
        Assert.Null(await CategorySlugOf("Carne Sem Palavra Chave")); // left for the classifier
        Assert.Equal("leite", await CategorySlugOf("Leite Meio Gordo"));
        var tags = await _db.ProductTags.Include(t => t.Product).Where(t => t.Product.Name == "Leite Sem Lactose Bio").Select(t => t.Tag).ToListAsync(Ct);
        Assert.Contains("sem-lactose", tags);
        Assert.Contains("bio", tags);
    }
}
