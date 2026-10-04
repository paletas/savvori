using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Savvori.Shared;
using Savvori.WebApi;
using Savvori.WebApi.Modeling;

namespace Savvori.Web.Tests.Modeling;

public sealed class AliasTests : IDisposable
{
    private readonly PipelineHost _h = new();
    public void Dispose() => _h.Dispose();

    private Guid AddProduct(string name, string? brand = null, bool active = true)
    {
        var (sp, canonical) = _h.AddListed(_h.ChainA, name, brand, active ? 1 : null);
        if (!active) _h.With(db => db.StoreProducts.Single(x => x.Id == sp).IsActive = false);
        return canonical;
    }

    private async Task<(int Products, int Todo, int Enqueued)> ScanAsync()
    {
        using var scope = _h.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AliasScanner>().ScanAsync(TestContext.Current.CancellationToken);
    }

    private List<ProductSearchAlias> Aliases(Guid productId) =>
        _h.Query(db => db.ProductSearchAliases.AsNoTracking().Where(a => a.ProductId == productId).ToList());

    [Fact]
    public async Task ScanAndDrain_StoreOneRowPerLanguage_WithFoldedSearchText()
    {
        var arroz = AddProduct("Arroz Agulha Cigala", "Cigala");
        _h.Translator.Answers["Arroz Agulha Cigala"] = new()
        {
            ["pt"] = ["Arroz", "arroz agulha"], ["en"] = ["Rice", "long grain rice"],
            ["es"] = ["Arroz"], ["fr"] = ["Riz", "riz à grain long"]
        };

        Assert.Equal(1, (await ScanAsync()).Enqueued);
        await _h.DrainAsync();

        var rows = Aliases(arroz).ToDictionary(a => a.Language);
        Assert.Equal(4, rows.Count);
        Assert.Equal("Rice", rows["en"].Name);
        Assert.Equal("Rice, long grain rice", rows["en"].Keywords);
        Assert.Equal("rice long grain rice", rows["en"].SearchText);
        Assert.Equal("riz riz a grain long", rows["fr"].SearchText); // accent-free, like the search term
        Assert.All(rows.Values, r =>
        {
            Assert.Equal("model", r.Source);
            Assert.Equal("fake-judge", r.ModelName);
            Assert.False(string.IsNullOrEmpty(r.InputHash));
        });
    }

    [Fact]
    public async Task Scan_QueuesNothing_OnceDone_AndRequeuesWhenTheNameChanges()
    {
        var id = AddProduct("Leite Mimosa");
        await ScanAsync();
        await _h.DrainAsync();

        Assert.Equal(0, (await ScanAsync()).Enqueued);

        _h.With(db => db.Products.Single(p => p.Id == id).Name = "Leite Mimosa Magro");
        Assert.Equal(1, (await ScanAsync()).Enqueued);
    }

    [Fact]
    public async Task Scan_IgnoresProductsWithoutAnActiveListing()
    {
        AddProduct("Produto Descontinuado", active: false);
        var scan = await ScanAsync();
        Assert.Equal(0, scan.Products);
        Assert.Equal(0, scan.Enqueued);
    }

    [Fact]
    public async Task Handler_SkipsObsoleteJobs_WhenTheProductChangedSinceQueueing()
    {
        var id = AddProduct("Leite Mimosa");
        await ScanAsync();
        _h.With(db => db.Products.Single(p => p.Id == id).Name = "Leite Mimosa Magro"); // job hash is now stale

        await _h.DrainAsync();

        Assert.Equal(0, _h.Translator.Calls);
        Assert.Empty(Aliases(id));
        Assert.All(_h.Query(db => db.ModelJobs.ToList()), j => Assert.Equal(ModelJobStatus.Done, j.Status));
    }

    [Fact]
    public async Task Handler_NeverOverwritesAManualRow_ButFillsTheOtherLanguages()
    {
        var id = AddProduct("Arroz Agulha Cigala");
        _h.With(db => db.ProductSearchAliases.Add(new ProductSearchAlias
        {
            ProductId = id, Language = "en", Name = "Pilaf base", Keywords = "pilaf base", SearchText = "pilaf base",
            Source = "manual", CreatedAt = DateTime.UtcNow
        }));
        _h.Translator.Answers["Arroz Agulha Cigala"] = new() { ["en"] = ["Rice"], ["fr"] = ["Riz"] };

        await ScanAsync();
        await _h.DrainAsync();

        var rows = Aliases(id).ToDictionary(a => a.Language);
        Assert.Equal("manual", rows["en"].Source);
        Assert.Equal("pilaf base", rows["en"].SearchText);
        Assert.Equal("riz", rows["fr"].SearchText);
        Assert.Equal("model", rows["fr"].Source);
    }

    [Fact]
    public async Task Scan_SkipsAProductCorrectedInEveryLanguage()
    {
        var id = AddProduct("Arroz Agulha Cigala");
        _h.With(db =>
        {
            foreach (var lang in OllamaProductTranslator.Languages)
                db.ProductSearchAliases.Add(new ProductSearchAlias
                    { ProductId = id, Language = lang, Source = "manual", CreatedAt = DateTime.UtcNow });
        });

        Assert.Equal(0, (await ScanAsync()).Enqueued);
    }

    [Fact]
    public async Task Scan_RequeuesAProductWhoseAliasWasReset()
    {
        var id = AddProduct("Arroz Agulha Cigala");
        await ScanAsync();
        await _h.DrainAsync();
        Assert.Equal(0, (await ScanAsync()).Enqueued);

        // What DELETE /aliases/{language} does: drop the row and mark the product's other model rows out of date.
        _h.With(db =>
        {
            db.ProductSearchAliases.RemoveRange(db.ProductSearchAliases.Where(a => a.ProductId == id && a.Language == "en"));
            foreach (var a in db.ProductSearchAliases.Where(a => a.ProductId == id)) a.InputHash = null;
        });

        Assert.Equal(1, (await ScanAsync()).Enqueued);
    }

    [Fact]
    public async Task Handler_BatchesSeveralProductsPerRequest()
    {
        _h.Options.Aliases.ProductsPerRequest = 3;
        for (var i = 0; i < 4; i++) AddProduct($"Produto {i}"); // the drain claims 4 jobs (BatchSize) per chunk

        await ScanAsync();
        await _h.DrainAsync();

        Assert.Equal([3, 1], _h.Translator.BatchSizes); // 4 products, 3 per request: 2 requests, not 4
    }

    [Fact]
    public async Task Scan_IsCappedPerRun()
    {
        _h.Options.Aliases.MaxJobsPerScan = 2;
        for (var i = 0; i < 5; i++) AddProduct($"Produto {i}");

        var scan = await ScanAsync();

        Assert.Equal(5, scan.Todo);
        Assert.Equal(2, scan.Enqueued);
    }

    [Fact]
    public async Task ABadModelAnswer_StoresNothing_AndKeepsTheBreakerClosed()
    {
        var id = AddProduct("Arroz Agulha Cigala");
        await ScanAsync();
        _h.Faults.GoDown(FaultMode.BadResponse);

        await _h.DrainAsync();

        Assert.Empty(Aliases(id));
        Assert.True(_h.Breaker.IsClosed); // reachable but bad is not an outage
        Assert.Contains(_h.Query(db => db.ModelJobs.ToList()), j => j.Status != ModelJobStatus.Done);
    }

    [Fact]
    public void RealModelServiceRegistration_ResolvesTheAliasScannerAndTranslateHandler()
    {
        // The other tests build their own container; this one uses the registration the app really runs,
        // because a missing line there only shows up as a Quartz job failing in production.
        var config = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<SavvoriDbContext>(o => o.UseInMemoryDatabase($"Reg_{Guid.NewGuid()}"));
        services.AddModelServices(config);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = false });
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<AliasScanner>());
        Assert.Contains(scope.ServiceProvider.GetServices<IModelJobHandler>(), h => h.Type == ModelJobType.Translate);
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IProductTranslator>());
    }

    [Fact]
    public void Clean_TrimsDedupesByFoldedFormAndCaps()
    {
        var cleaned = AliasInputs.Clean([" Rice ", "rice", "RÍCE", "", "  ", new string('x', 60), "a", "b", "c", "d", "e"]);
        Assert.Equal(["Rice", "a", "b", "c", "d"], cleaned);
    }

    [Fact]
    public void Parse_RequiresOneEntryPerIndex()
    {
        var ok = OllamaProductTranslator.Parse(
            """{"products":[{"index":1,"pt":[],"en":["milk"],"es":[],"fr":[]},{"index":0,"pt":["arroz"],"en":[],"es":[],"fr":[]}]}""", 2);
        Assert.Equal(["arroz"], ok[0].Keywords["pt"]);
        Assert.Equal(["milk"], ok[1].Keywords["en"]);

        Assert.Throws<ModelResponseException>(() => OllamaProductTranslator.Parse("""{"products":[]}""", 1));
        Assert.Throws<ModelResponseException>(() => OllamaProductTranslator.Parse("not json", 1));
        Assert.Throws<ModelResponseException>(() => OllamaProductTranslator.Parse(
            """{"products":[{"index":0,"pt":[]},{"index":0,"pt":[]}]}""", 2)); // duplicate index
    }
}
