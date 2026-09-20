using Savvori.WebApi.Scraping;
using Savvori.Shared;

namespace Savvori.Web.Tests;

public class ProductNormalizerTests
{
    [Theory]
    [InlineData("Leite UHT Meio Gordo", "leite uht meio gordo")]
    [InlineData("Iogurte Grego Açúcar", "iogurte grego acucar")]
    [InlineData("Água   Mineral", "agua mineral")]
    [InlineData("  Maçã  Fuji  ", "maca fuji")]
    [InlineData("Café Espresso", "cafe espresso")]
    public void Normalize_RemovesAccentsAndNormalizesSpaces(string input, string expected)
    {
        var result = ProductNormalizer.Normalize(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Leite UHT 1L", 1.0, (int)ProductUnit.L)]
    [InlineData("Iogurte 500g", 500, (int)ProductUnit.G)]
    [InlineData("Sumo 1.5L", 1.5, (int)ProductUnit.L)]
    [InlineData("Carne 250gr", 250, (int)ProductUnit.G)]
    [InlineData("Água 1.5 lt", 1.5, (int)ProductUnit.L)]
    [InlineData("Queijo 200 g", 200, (int)ProductUnit.G)]
    [InlineData("Manteiga 250 GRS", 250, (int)ProductUnit.G)]
    public void ExtractSizeAndUnit_ParsesCorrectly(string name, double expectedSize, int expectedUnit)
    {
        var result = ProductNormalizer.ExtractSizeAndUnit(name);

        Assert.NotNull(result);
        Assert.Equal((decimal)expectedSize, result.Value.SizeValue);
        Assert.Equal((ProductUnit)expectedUnit, result.Value.Unit);
    }

    [Theory]
    [InlineData("Refrigerante Coca-Cola Zero 0,5L", 0.5, (int)ProductUnit.L)]
    [InlineData("Sumo Compal 1,5 L", 1.5, (int)ProductUnit.L)]
    [InlineData("Batata Doce Baby 1,5 kg", 1.5, (int)ProductUnit.Kg)]
    [InlineData("Leite Meio Gordo 0,245 L", 0.245, (int)ProductUnit.L)]
    [InlineData("Cerveja Sagres 33cl", 330, (int)ProductUnit.Ml)]
    [InlineData("Cerveja Sagres 33,5cl", 335, (int)ProductUnit.Ml)]
    [InlineData("Arroz Agulha 300 g", 300, (int)ProductUnit.G)]
    [InlineData("Presunto 0,25 kg", 0.25, (int)ProductUnit.Kg)]
    [InlineData("Cerveja Sagres 6x33cl", 1980, (int)ProductUnit.Ml)]
    [InlineData("Cerveja Sagres 6 x 33 cl", 1980, (int)ProductUnit.Ml)]
    [InlineData("Água 6x1,5L", 9, (int)ProductUnit.L)]
    [InlineData("Cerveja Sagres Pack 6 Latas 33cl", 1980, (int)ProductUnit.Ml)]
    [InlineData("Coca-Cola Pack 6", 6, (int)ProductUnit.Pack)]
    [InlineData("Iogurtes 4 un", 4, (int)ProductUnit.Unit)]
    public void ExtractSizeAndUnit_HandlesDecimalCommasAndMultipacks(string name, double expectedSize, int expectedUnit)
    {
        var result = ProductNormalizer.ExtractSizeAndUnit(name);

        Assert.NotNull(result);
        Assert.Equal((decimal)expectedSize, result.Value.SizeValue);
        Assert.Equal((ProductUnit)expectedUnit, result.Value.Unit);
    }

    [Theory]
    // parsed size agrees with the store's unit price: keep it
    [InlineData(1.19, 2.38, 0.5, (int)ProductUnit.L, 0.5, false)]
    // dropped decimal comma: "5 L" but €1.19 at €2.38/L is 0.5 L
    [InlineData(1.19, 2.38, 5, (int)ProductUnit.L, 0.5, true)]
    [InlineData(0.99, 1.98, 5, (int)ProductUnit.Kg, 0.5, true)]
    // grams: 5 g vs implied 500 g
    [InlineData(1.99, 3.98, 5, (int)ProductUnit.G, 500, true)]
    [InlineData(0.6, 2.45, 245, (int)ProductUnit.Ml, 245, false)]
    // within 5% (unit price rounding) is not a disagreement
    [InlineData(1.99, 3.98, 500, (int)ProductUnit.G, 500, false)]
    [InlineData(1.0, 3.9, 250, (int)ProductUnit.G, 250, false)]
    public void ReconcileSizeWithUnitPrice_PrefersStoreUnitPrice(
        double price, double unitPrice, double size, int unit, double expectedSize, bool expectedDisagreed)
    {
        var result = ProductNormalizer.ReconcileSizeWithUnitPrice(
            (decimal)price, (decimal)unitPrice, (decimal)size, (ProductUnit)unit);

        Assert.Equal(expectedDisagreed, result.Disagreed);
        Assert.Equal((decimal)expectedSize, result.SizeValue!.Value, 3);
        Assert.Equal((ProductUnit)unit, result.Unit);
    }

    [Fact]
    public void ReconcileSizeWithUnitPrice_LeavesSizeAloneWithoutUnitPriceOrForCountUnits()
    {
        var noUnitPrice = ProductNormalizer.ReconcileSizeWithUnitPrice(1.19m, null, 5m, ProductUnit.L);
        Assert.False(noUnitPrice.Disagreed);
        Assert.Equal(5m, noUnitPrice.SizeValue);

        var count = ProductNormalizer.ReconcileSizeWithUnitPrice(2m, 0.5m, 6m, ProductUnit.Pack);
        Assert.False(count.Disagreed);
        Assert.Equal(6m, count.SizeValue);
    }

    [Theory]
    [InlineData("Produto sem tamanho")]
    [InlineData("Manteiga sem sal")]
    [InlineData("Vinho Tinto 2019")]
    [InlineData("")]
    public void ExtractSizeAndUnit_ReturnsNull_WhenNoSizeFound(string name)
    {
        var result = ProductNormalizer.ExtractSizeAndUnit(name);
        Assert.Null(result);
    }

    [Theory]
    [InlineData(1.0, (int)ProductUnit.L, 1.0, 1.0)]   // 1L at €1 → €1/L
    [InlineData(0.9, (int)ProductUnit.L, 1.0, 0.9)]    // 1L at €0.90 → €0.90/L
    [InlineData(1.5, (int)ProductUnit.G, 500, 3.0)]    // 500g at €1.50 → €3/kg
    [InlineData(2.0, (int)ProductUnit.Kg, 1.0, 2.0)]   // 1kg at €2 → €2/kg
    [InlineData(0.5, (int)ProductUnit.Ml, 250, 2.0)]   // 250ml at €0.50 → €2/L
    public void ComputeUnitPrice_ReturnsCorrectValue(
        double price, int unit, double size, double expectedPerUnit)
    {
        var result = ProductNormalizer.ComputeUnitPrice((decimal)price, (ProductUnit)unit, (decimal)size);
        Assert.NotNull(result);
        Assert.Equal((decimal)expectedPerUnit, result!.Value, 2);
    }

    [Fact]
    public void ComputeUnitPrice_ReturnsNull_WhenSizeIsNull()
    {
        var result = ProductNormalizer.ComputeUnitPrice(1.0m, ProductUnit.L, null);
        Assert.Null(result);
    }

    [Fact]
    public void ComputeUnitPrice_ReturnsNull_WhenSizeIsZero()
    {
        var result = ProductNormalizer.ComputeUnitPrice(1.0m, ProductUnit.L, 0m);
        Assert.Null(result);
    }

    [Fact]
    public void ComputeUnitPrice_ReturnsNull_ForUnitType()
    {
        var result = ProductNormalizer.ComputeUnitPrice(1.99m, ProductUnit.Unit, 6m);
        Assert.Null(result);
    }
}
