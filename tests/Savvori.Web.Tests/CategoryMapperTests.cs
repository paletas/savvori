using Savvori.WebApi.Scraping;

namespace Savvori.Web.Tests;

public class CategoryMapperTests
{
    [Fact]
    public void MapToSlug_LeiteUht_ReturnsLeite()
        => Assert.Equal("leite", CategoryMapper.MapToSlug("leite-uht"));

    [Fact]
    public void MapToSlug_Iogurte_ReturnsIogurtes()
        => Assert.Equal("iogurtes", CategoryMapper.MapToSlug("iogurte"));

    [Fact]
    public void MapToSlug_Queijo_ReturnsQueijos()
        => Assert.Equal("queijos", CategoryMapper.MapToSlug("queijo"));

    [Fact]
    public void MapToSlug_Carne_ReturnsCarne()
        => Assert.Equal("carne", CategoryMapper.MapToSlug("carne"));

    [Fact]
    public void MapToSlug_Mercearia_ReturnsMercearia()
        => Assert.Equal("mercearia", CategoryMapper.MapToSlug("mercearia"));

    [Fact]
    public void MapToSlug_Null_ReturnsNull()
        => Assert.Null(CategoryMapper.MapToSlug(null));

    [Fact]
    public void MapToSlug_Empty_ReturnsNull()
        => Assert.Null(CategoryMapper.MapToSlug(""));

    [Fact]
    public void MapToSlug_Whitespace_ReturnsNull()
        => Assert.Null(CategoryMapper.MapToSlug("   "));

    [Fact]
    public void MapToSlug_Unknown_ReturnsNull()
        => Assert.Null(CategoryMapper.MapToSlug("xyz-category-unknown-12345"));

    [Fact]
    public void MapToSlug_CaseInsensitive_Works()
        => Assert.Equal("leite", CategoryMapper.MapToSlug("LEITE"));

    [Fact]
    public void MapToSlug_PartialMatch_Works()
    {
        // Input is a path containing "leite" → should resolve to "leite"
        var result = CategoryMapper.MapToSlug("produtos-lacteos/leite-uht");
        Assert.Equal("leite", result);
    }

    [Fact]
    public void MapToSlug_IogurtesSolidos_ReturnsIogurtes()
        => Assert.Equal("iogurtes", CategoryMapper.MapToSlug("iogurtes-solidos"));

    [Fact]
    public void MapToSlug_Manteiga_ReturnsManteigarMargarinas()
        => Assert.Equal("manteiga-margarinas", CategoryMapper.MapToSlug("manteiga"));

    [Fact]
    public void MapToSlug_Arroz_ReturnsArroz()
        => Assert.Equal("arroz", CategoryMapper.MapToSlug("arroz"));

    [Fact]
    public void MapToSlug_Agua_ReturnsAgua()
        => Assert.Equal("agua", CategoryMapper.MapToSlug("água"));

    [Fact]
    public void MapToSlug_Cerveja_ReturnsBedidasAlcoolicas()
        => Assert.Equal("bebidas-alcoolicas", CategoryMapper.MapToSlug("cerveja"));

    [Fact]
    public void MapToSlug_Limpeza_ReturnsLimpezaLar()
        => Assert.Equal("limpeza-lar", CategoryMapper.MapToSlug("limpeza"));

    [Fact]
    public void MapToSlug_Higiene_ReturnsHigienePessoal()
        => Assert.Equal("higiene-pessoal", CategoryMapper.MapToSlug("higiene"));

    [Fact]
    public void MapToSlug_Detergente_ReturnsDetergentes()
        => Assert.Equal("detergentes", CategoryMapper.MapToSlug("detergente"));
}
