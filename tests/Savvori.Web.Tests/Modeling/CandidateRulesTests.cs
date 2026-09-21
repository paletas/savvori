using Savvori.Shared;
using Savvori.WebApi.Modeling;

namespace Savvori.Web.Tests.Modeling;

public sealed class CandidateRulesTests
{
    private static ListingFacts Item(string name = "x", string? brand = null, decimal? size = null,
        ProductUnit unit = ProductUnit.Unit) =>
        new(Guid.NewGuid(), Guid.NewGuid(), null, name, brand, size, unit);

    [Theory]
    [InlineData(1, ProductUnit.Kg, 1000, ProductUnit.G, SizeVerdict.Compatible)]     // same mass
    [InlineData(1.5, ProductUnit.L, 1500, ProductUnit.Ml, SizeVerdict.Compatible)]
    [InlineData(500, ProductUnit.G, 505, ProductUnit.G, SizeVerdict.Compatible)]     // 1% apart
    [InlineData(500, ProductUnit.G, 520, ProductUnit.G, SizeVerdict.Conflict)]       // 3.8% apart
    [InlineData(500, ProductUnit.G, 500, ProductUnit.Ml, SizeVerdict.Conflict)]      // mass vs volume
    [InlineData(6, ProductUnit.Pack, 6, ProductUnit.Unit, SizeVerdict.Compatible)]   // both count
    [InlineData(6, ProductUnit.Unit, 12, ProductUnit.Unit, SizeVerdict.Conflict)]
    [InlineData(1, ProductUnit.L, 1000, ProductUnit.G, SizeVerdict.Conflict)]
    public void CompareSizes_BothKnown(double a, ProductUnit ua, double b, ProductUnit ub, SizeVerdict expected) =>
        Assert.Equal(expected, CandidateRules.CompareSizes(
            Item(size: (decimal)a, unit: ua), Item(size: (decimal)b, unit: ub), 0.02));

    [Fact]
    public void CompareSizes_EitherUnknown_IsUnknownNotRejected()
    {
        Assert.Equal(SizeVerdict.Unknown, CandidateRules.CompareSizes(Item(), Item(size: 1, unit: ProductUnit.L), 0.02));
        Assert.Equal(SizeVerdict.Unknown, CandidateRules.CompareSizes(Item(size: 0), Item(size: 0), 0.02));
    }

    [Fact]
    public void CompareSizes_ToleranceIsConfigurable() =>
        Assert.Equal(SizeVerdict.Compatible, CandidateRules.CompareSizes(
            Item(size: 500, unit: ProductUnit.G), Item(size: 520, unit: ProductUnit.G), 0.05));

    [Theory]
    [InlineData("Mimosa", "mimosa", BrandVerdict.Ok)]
    [InlineData("Pingo Doce", "Pingo Doce Bio", BrandVerdict.Ok)]           // token subset
    [InlineData("Coca-Cola", "Coca Cola", BrandVerdict.Ok)]                  // punctuation ignored
    [InlineData("Nestlé", "Nestle", BrandVerdict.Ok)]                        // accents ignored
    [InlineData("Continente", "Pingo Doce", BrandVerdict.Conflict)]          // rival own brands
    [InlineData("Mimosa", "Agros", BrandVerdict.Conflict)]
    public void CompareBrands_BothPresent(string a, string b, BrandVerdict expected) =>
        Assert.Equal(expected, CandidateRules.CompareBrands(Item(brand: a), Item(brand: b)));

    [Fact]
    public void CompareBrands_OneMissing_OkWhenBrandAppearsInTheOthersName()
    {
        Assert.Equal(BrandVerdict.Ok, CandidateRules.CompareBrands(
            Item(brand: "Mimosa"), Item(name: "Leite Mimosa Meio Gordo 1L")));
        Assert.Equal(BrandVerdict.Ok, CandidateRules.CompareBrands(
            Item(name: "Leite Mimosa Meio Gordo 1L"), Item(brand: "Mimosa")));
    }

    [Fact]
    public void CompareBrands_OneMissing_UnknownWhenNotInName()
    {
        Assert.Equal(BrandVerdict.Unknown, CandidateRules.CompareBrands(
            Item(brand: "Mimosa"), Item(name: "Leite Meio Gordo 1L")));
    }

    [Theory]
    [InlineData("Tortitas Chocolate Negro", "Tortitas Chocolate Negro sem Açúcar", true)]
    [InlineData("Leite Meio Gordo", "Leite Sem Lactose Meio Gordo", true)]
    [InlineData("Arroz", "Arroz Bio", false)]                              // bio is labelled inconsistently: not compared
    [InlineData("Massa Bio", "Massa Bio Sem Glúten", true)]
    [InlineData("Massa Sem Glúten", "Massa sem Gluten Fusilli", false)]   // both gluten free
    [InlineData("Bolachas Maria", "Bolacha Maria Dourada", false)]         // no dietary tags either side
    public void TagsConflict_WhenOneSideIsADietaryVariantAndTheOtherIsNot(string a, string b, bool conflict) =>
        Assert.Equal(conflict, CandidateRules.TagsConflict(Item(name: a), Item(name: b)));

    [Fact]
    public void CompareBrands_BothMissing_IsUnknown() =>
        Assert.Equal(BrandVerdict.Unknown, CandidateRules.CompareBrands(Item(), Item()));
}
