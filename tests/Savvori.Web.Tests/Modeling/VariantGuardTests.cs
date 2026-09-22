using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Savvori.Shared;
using Savvori.WebApi;
using Savvori.WebApi.Modeling;

namespace Savvori.Web.Tests.Modeling;

/// <summary>
/// The pairs come from real beta data (bulk-applied merges that were right, and pending pairs that were different
/// variants), so a change to the word lists that breaks one of them is a real regression.
/// </summary>
public sealed class VariantGuardTests
{
    [Theory]
    // different flavours / varieties / percentages
    [InlineData("Água com Gás Pedras Ananás", "Pedras Salgadas", "Água com Gás Limão Pedras Salgadas", "Pedras Salgadas")]
    [InlineData("Sumo de 100% Maçã Compal 100% Fruta", "Compal", "Sumo de 100% Laranja", "Compal")]
    [InlineData("Tablete de Chocolate Negro Excellence 70% Cacau", "Lindt", "Tablete de Chocolate Excellence 85% Cacau", "Lindt")]
    [InlineData("Manteiga de Amendoim Crocante Prozis", "Prozis", "Manteiga de Amendoim Cremosa", null)]
    [InlineData("Mini Pizzas Piccolinis Salame", "Buitoni", "Mini Pizzas de Salsicha Buitoni Piccolinis", "Buitoni")]
    [InlineData("Barrinhas de Salmão Panado Sem Glúten", "Pescanova", "Barrinhas de Pescada sem Glúten Pescanova", "Pescanova")]
    [InlineData("Azeite Virgem Extra Sublime Gallo", "Gallo", "Azeite Virgem Extra Clássico", "Gallo")]
    [InlineData("Bebida Vegetal de Soja Proteína Alpro", "Alpro", "Bebida Vegetal de Soja Chocolate", "Alpro")]
    // one side carries a variant marker the other lacks
    [InlineData("Queijo para Barrar Philadelphia", "Philadelphia", "Queijo para Barrar Light", "Philadelphia")]
    [InlineData("Leite Meio Gordo UHT Infantil +3A Mimosa", "Mimosa", "Leite Mimosa UHT Meio Gordo 1L", "Mimosa")]
    [InlineData("Bebida Vegetal de Aveia Oatly", "Oatly", "BEBIDA AVEIA OATLY BARISTA BIO 1LT", "OATLY BARISTA")]   // the marker sits in the brand field
    [InlineData("Bolachas Crackers sem Sal na Superfície", "Gran Pavesi", "Bolachas Crackers com Sal na Superfície", "Gran Pavesi")]
    // one side has an extra ingredient/flavour word the other lacks entirely (prod sample, 2026-09-23)
    [InlineData("Bebida Vegetal de Aveia Alpro", "Alpro", "Bebida Vegetal de Aveia e Amêndoa", "Alpro")]
    [InlineData("Bebida Vegetal de Arroz Alpro", "Alpro", "Bebida Vegetal de Arroz e Coco", "Alpro")]
    [InlineData("Bebida Vegetal de Soja Proteína Alpro", "Alpro", "Bebida Vegetal de Soja", "Alpro")]
    [InlineData("Bebida Vegetal de Soja Chocolate Alpro", "Alpro", "Bebida Vegetal de Soja", "Alpro")]
    [InlineData("Água Tónica Pink Zero", "Schweppes", "Água Tónica Zero", "Schweppes")]
    [InlineData("Queijo Fundido Palitos Pizza", "A Vaca que ri", "Queijo Fundido Palitos", "A Vaca que ri")]
    [InlineData("Ovo Chocolate de Leite com Surpresa Joy Kinder", "Kinder", "Ovos de Chocolate de Leite Kinder Surpresa", "Kinder")]
    public void DifferentVariants_AreFlagged(string a, string? brandA, string b, string? brandB) =>
        Assert.True(VariantGuard.Compare(a, brandA, b, brandB).Conflict);

    [Theory]
    // word order, plural, filler words, sizes, the brand held in the other listing's name field
    [InlineData("Néctar Pera Rocha Compal Clássico", "Compal Clássico", "Néctar de Pera Rocha", "Compal Clássico")]
    [InlineData("Flocos de Aveia Integral Grossos", "Cem Porcento", "Flocos Aveia Grossos Integral Cem Porcento", "Cem Porcento")]
    [InlineData("Tablete de Chocolate Negro Suave Excellence 70", "Lindt", "Tablete de Chocolate Negro Excellence Suave 70", "Lindt")]
    [InlineData("CALDO KNORR CARNE 8 CUBOS 80G", "KNORR", "Caldo de Carne 8 Cubos", "Knorr")]
    [InlineData("Uva Branca sem Grainha Biológica Hey, Vita!", "Hey, Vita!", "Uva Branca sem Grainha Bio", "Hey, Vita!")]
    [InlineData("Kéfir de Mirtilos", null, "Kefir Mirtilo Activia Danone", "Activia Danone")]
    [InlineData("Bebida Láctea com sabor a Bolacha 1-3A Mimosa", "Mimosa", "Bebida Láctea com sabor a Bolacha 1-3 Anos", "Mimosa")]
    [InlineData("Água sem Gás Voss", "Voss", "Água sem Gás", "Voss")]
    [InlineData("Bebida Aveia Oatly Barista", "Oatly", "BEBIDA AVEIA OATLY BARISTA BIO 1LT", "OATLY BARISTA")]
    public void SameProduct_IsNotFlagged_AndIsIdentical(string a, string? brandA, string b, string? brandB)
    {
        var v = VariantGuard.Compare(a, brandA, b, brandB);
        Assert.False(v.Conflict);
        Assert.True(v.Identical);
    }

    [Fact]
    public void AnExtraWordOnOneSide_IsNotAConflict_ButIsNotIdenticalEither()
    {
        var v = VariantGuard.Compare("Arroz Agulha Extra Longo Caçarola", "Cigala", "Arroz Agulha", "Cigala");
        Assert.False(v.Conflict);
        Assert.False(v.Identical);
    }
}

public sealed class BulkVariantTests : IDisposable
{
    private readonly PipelineHost _h = new();
    public void Dispose() => _h.Dispose();

    private Guid Suggested(string nameA, string nameB, double cosine, string suggestion = "embedding-cosine")
    {
        var (a, _) = _h.AddListed(_h.ChainA, nameA);
        var (b, _) = _h.AddListed(_h.ChainB, nameB);
        var id = _h.AddCandidate(a, b, cosine);
        _h.With(db =>
        {
            var c = db.MatchCandidates.Single(x => x.Id == id);
            c.Status = CandidateStatus.NeedsReview;
            c.Suggestion = suggestion;
        });
        return id;
    }

    private async Task<List<MatchBulkService.Pick>> PickAsync(double min, double? floor)
    {
        using var scope = _h.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MatchBulkService>().PickAsync(min, floor, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ConfidentPairsWithDifferentVariants_AreNotPicked()
    {
        var same = Suggested("Água com Gás Limão", "Água Gás Limão", 0.95);
        Suggested("Água com Gás Ananás", "Água com Gás Limão", 0.95);

        var picks = await PickAsync(0.90, null);

        Assert.Equal([same], picks.Select(p => p.Id));
    }

    [Fact]
    public async Task ExactFloor_AddsIdenticalNamedPairsBelowTheThreshold_AndNothingElse()
    {
        var strict = Suggested("Leite Meio Gordo", "Leite Gordo Meio", 0.95);
        var exact = Suggested("Massa Linguine", "Massa Linguine", 0.86, suggestion: null!);
        Suggested("Massa Linguine Integral", "Massa Linguine Tricolor", 0.86, suggestion: null!);     // different variants
        Suggested("Massa Fusilli", "Massa Fusilli", 0.78, suggestion: null!);                         // below the floor
        var judgedNo = Suggested("Massa Penne", "Massa Penne", 0.86, suggestion: null!);
        _h.With(db => db.MatchCandidates.Single(x => x.Id == judgedNo).JudgeVerdict = JudgeVerdict.No);

        var without = await PickAsync(0.90, null);
        var with = await PickAsync(0.90, 0.80);

        Assert.Equal([strict], without.Select(p => p.Id));
        Assert.Equal([strict, exact], with.Select(p => p.Id));
        Assert.True(with.Single(p => p.Id == exact).Exact);
    }

    [Fact]
    public async Task RunWithAnExactFloor_MergesThem_RecordsTheMethod_AndUndoRestoresThem()
    {
        var exact = Suggested("Massa Linguine", "Massa Linguine", 0.86, suggestion: null!);
        using var scope = _h.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<MatchBulkService>();
        var ct = TestContext.Current.CancellationToken;

        var batch = await svc.CreateAsync(0.90, 0.80, ct);
        await svc.RunApplyAsync(batch.Id, 0.80, ct);

        Assert.Equal(1, batch.Total);
        Assert.Equal("embedding-exact-name", _h.Candidate(exact).Method);
        Assert.Equal(CandidateStatus.Applied, _h.Candidate(exact).Status);

        await svc.RunUndoAsync(batch.Id, ct);
        Assert.Equal(CandidateStatus.NeedsReview, _h.Candidate(exact).Status);
    }
}
