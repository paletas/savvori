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
    [InlineData("Produto sem tamanho")]
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
