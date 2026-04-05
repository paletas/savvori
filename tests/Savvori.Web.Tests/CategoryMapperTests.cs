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

    // ── New entries: Chocolates e Confeitaria ───────────────────────────────

    [Fact]
    public void MapToSlug_Tabletes_ReturnsBolachas()
        => Assert.Equal("bolachas", CategoryMapper.MapToSlug("Tabletes"));

    [Fact]
    public void MapToSlug_PepitasRecheadasCobertas_ReturnsBolachas()
        => Assert.Equal("bolachas", CategoryMapper.MapToSlug("Pepitas, Recheadas e Cobertas"));

    [Fact]
    public void MapToSlug_Gomas_ReturnsBolachas()
        => Assert.Equal("bolachas", CategoryMapper.MapToSlug("Gomas"));

    [Fact]
    public void MapToSlug_Wafers_ReturnsBolachas()
        => Assert.Equal("bolachas", CategoryMapper.MapToSlug("Wafers"));

    [Fact]
    public void MapToSlug_BatatsFritas_ReturnsBolachas()
        => Assert.Equal("bolachas", CategoryMapper.MapToSlug("Batatas fritas"));

    // ── New entries: Iogurtes subcategorias ────────────────────────────────

    [Fact]
    public void MapToSlug_Liquidos_ReturnsIogurtes()
        => Assert.Equal("iogurtes", CategoryMapper.MapToSlug("Líquidos"));

    [Fact]
    public void MapToSlug_Gregos_ReturnsIogurtes()
        => Assert.Equal("iogurtes", CategoryMapper.MapToSlug("Gregos"));

    [Fact]
    public void MapToSlug_Naturais_ReturnsIogurtes()
        => Assert.Equal("iogurtes", CategoryMapper.MapToSlug("Naturais"));

    [Fact]
    public void MapToSlug_BifidusEFuncionais_ReturnsIogurtes()
        => Assert.Equal("iogurtes", CategoryMapper.MapToSlug("Bífidus e Funcionais"));

    [Fact]
    public void MapToSlug_Kefir_ReturnsIogurtes()
        => Assert.Equal("iogurtes", CategoryMapper.MapToSlug("Kéfir"));

    // ── New entries: Queijos subcategorias ─────────────────────────────────

    [Fact]
    public void MapToSlug_Ralado_ReturnsQueijos()
        => Assert.Equal("queijos", CategoryMapper.MapToSlug("Ralado"));

    [Fact]
    public void MapToSlug_FrescoERequeijao_ReturnsQueijos()
        => Assert.Equal("queijos", CategoryMapper.MapToSlug("Fresco e Requeijão"));

    // ── New entries: Carne / Peixe ─────────────────────────────────────────

    [Fact]
    public void MapToSlug_NovilhoEBovino_ReturnsCarne()
        => Assert.Equal("carne", CategoryMapper.MapToSlug("Novilho e Bovino"));

    [Fact]
    public void MapToSlug_Angus_ReturnsCarne()
        => Assert.Equal("carne", CategoryMapper.MapToSlug("Angus"));

    [Fact]
    public void MapToSlug_Pescada_ReturnsPeixeMarisco()
        => Assert.Equal("peixe-marisco", CategoryMapper.MapToSlug("Pescada"));

    [Fact]
    public void MapToSlug_DouradaERobalo_ReturnsPeixeMarisco()
        => Assert.Equal("peixe-marisco", CategoryMapper.MapToSlug("Dourada e Robalo"));

    // ── New entries: Frutas / Legumes ──────────────────────────────────────

    [Fact]
    public void MapToSlug_MelaoMelanciaEMeloa_ReturnsFrutas()
        => Assert.Equal("frutas", CategoryMapper.MapToSlug("Melão, Melancia e Meloa"));

    [Fact]
    public void MapToSlug_BananasPErasEMacas_ReturnsFrutas()
        => Assert.Equal("frutas", CategoryMapper.MapToSlug("Bananas, Peras e Maçãs"));

    [Fact]
    public void MapToSlug_BatatasCebolasEAlhos_ReturnsLegumes()
        => Assert.Equal("legumes", CategoryMapper.MapToSlug("Batatas, Cebolas e Alhos"));

    [Fact]
    public void MapToSlug_TomatosPepinosEPimentos_ReturnsLegumes()
        => Assert.Equal("legumes", CategoryMapper.MapToSlug("Tomates, Pepinos e Pimentos"));

    [Fact]
    public void MapToSlug_Alfaces_ReturnsLegumes()
        => Assert.Equal("legumes", CategoryMapper.MapToSlug("Alfaces"));

    // ── New entries: Conservas ─────────────────────────────────────────────

    [Fact]
    public void MapToSlug_CompotasEDoces_ReturnsConservas()
        => Assert.Equal("conservas", CategoryMapper.MapToSlug("Compotas e Doces"));

    [Fact]
    public void MapToSlug_AzeitонasPicklesETremocos_ReturnsConservas()
        => Assert.Equal("conservas", CategoryMapper.MapToSlug("Azeitonas, Pickles e Tremoços"));

    // ── New entries: Pão / Bebidas / Congelados ────────────────────────────

    [Fact]
    public void MapToSlug_FatiadoEBola_ReturnsPao()
        => Assert.Equal("pao", CategoryMapper.MapToSlug("Fatiado e Bola"));

    [Fact]
    public void MapToSlug_Noodles_ReturnsMassas()
        => Assert.Equal("massas", CategoryMapper.MapToSlug("Noodles"));

    [Fact]
    public void MapToSlug_Gelatina_ReturnsBоlosSobremesas()
        => Assert.Equal("bolos-sobremesas", CategoryMapper.MapToSlug("Gelatina"));

    [Fact]
    public void MapToSlug_Pizzas_ReturnsRefeicoesProntas()
        => Assert.Equal("refeicoes-prontas", CategoryMapper.MapToSlug("Pizzas"));

    [Fact]
    public void MapToSlug_EnergeticasEIsotonicas_ReturnsSumos()
        => Assert.Equal("sumos", CategoryMapper.MapToSlug("Energéticas e Isotónicas"));

    [Fact]
    public void MapToSlug_Achocolatados_ReturnsSumos()
        => Assert.Equal("sumos", CategoryMapper.MapToSlug("Achocolatados"));

    [Fact]
    public void MapToSlug_BebidaDeSoja_ReturnsSumos()
        => Assert.Equal("sumos", CategoryMapper.MapToSlug("Bebida de Soja"));

    [Fact]
    public void MapToSlug_Gelo_ReturnsCongelados()
        => Assert.Equal("congelados", CategoryMapper.MapToSlug("Gelo"));

    // ── New entries: Mercearia / Limpeza ───────────────────────────────────

    [Fact]
    public void MapToSlug_Mel_ReturnsMercearia()
        => Assert.Equal("mercearia", CategoryMapper.MapToSlug("Mel"));

    [Fact]
    public void MapToSlug_SacosDeCompras_ReturnsLimpezaLar()
        => Assert.Equal("limpeza-lar", CategoryMapper.MapToSlug("Sacos e Sacos de Compras"));

    // ── Peixe Congelado / Refeições Prontas ────────────────────────────────

    [Fact]
    public void MapToSlug_Douradinhos_ReturnsPeixeCongelado()
        => Assert.Equal("peixe-congelado", CategoryMapper.MapToSlug("Douradinhos e Filetes"));

    [Fact]
    public void MapToSlug_ProntoACozinhar_ReturnsRefeicoesProntas()
        => Assert.Equal("refeicoes-prontas", CategoryMapper.MapToSlug("Pronto a Cozinhar"));

    [Fact]
    public void MapToSlug_NuggetsECrocantes_ReturnsRefeicoesProntas()
        => Assert.Equal("refeicoes-prontas", CategoryMapper.MapToSlug("Nuggets e Crocantes"));

    // ── Conservas / Charcutaria ────────────────────────────────────────────

    [Fact]
    public void MapToSlug_TomatePolpa_ReturnsConservas()
        => Assert.Equal("conservas", CategoryMapper.MapToSlug("Tomate Polpa, Pelado e Seco"));

    [Fact]
    public void MapToSlug_Curado_ReturnsCharcutaria()
        => Assert.Equal("charcutaria", CategoryMapper.MapToSlug("Curado"));

    [Fact]
    public void MapToSlug_BaconEFumados_ReturnsCharcutaria()
        => Assert.Equal("charcutaria", CategoryMapper.MapToSlug("Bacon e Fumados"));

    [Fact]
    public void MapToSlug_AlheiraEFarinheira_ReturnsCharcutaria()
        => Assert.Equal("charcutaria", CategoryMapper.MapToSlug("Alheira e Farinheira"));

    [Fact]
    public void MapToSlug_FrescoECozido_ReturnsCharcutaria()
        => Assert.Equal("charcutaria", CategoryMapper.MapToSlug("Fresco e Cozido"));

    // ── Frutas / Legumes / Carne / Peixe ──────────────────────────────────

    [Fact]
    public void MapToSlug_BananaMacaEPera_ReturnsFrutas()
        => Assert.Equal("frutas", CategoryMapper.MapToSlug("Banana, Maçã e Pera"));

    [Fact]
    public void MapToSlug_UvasETropicais_ReturnsFrutas()
        => Assert.Equal("frutas", CategoryMapper.MapToSlug("Uvas e Tropicais"));

    [Fact]
    public void MapToSlug_PessegoAmeixaEKiwi_ReturnsFrutas()
        => Assert.Equal("frutas", CategoryMapper.MapToSlug("Pêssego, Ameixa e Kiwi"));

    [Fact]
    public void MapToSlug_CebоlaAlhoENabo_ReturnsLegumes()
        => Assert.Equal("legumes", CategoryMapper.MapToSlug("Cebola, Alho e Nabo"));

    [Fact]
    public void MapToSlug_CenouraАboboraEBeterraba_ReturnsLegumes()
        => Assert.Equal("legumes", CategoryMapper.MapToSlug("Cenoura, Abóbora e Beterraba"));

    [Fact]
    public void MapToSlug_BatataBatatаDoceEMandioca_ReturnsLegumes()
        => Assert.Equal("legumes", CategoryMapper.MapToSlug("Batata, Batata Doce e Mandioca"));

    [Fact]
    public void MapToSlug_PatoECoelho_ReturnsCarne()
        => Assert.Equal("carne", CategoryMapper.MapToSlug("Pato e Coelho"));

    [Fact]
    public void MapToSlug_FiletesLombosEPostas_ReturnsPeixeMarisco()
        => Assert.Equal("peixe-marisco", CategoryMapper.MapToSlug("Filetes, Lombos e Postas"));

    // ── Iogurtes / Cereais / Massas ────────────────────────────────────────

    [Fact]
    public void MapToSlug_VegegurtesEYofu_ReturnsIogurtes()
        => Assert.Equal("iogurtes", CategoryMapper.MapToSlug("Vegegurtes e Yofu"));

    [Fact]
    public void MapToSlug_CouscousQuinoaBulgur_ReturnsArroz()
        => Assert.Equal("arroz", CategoryMapper.MapToSlug("Couscous, Quinoa, Bulgur e Outros"));

    [Fact]
    public void MapToSlug_Italianas_ReturnsMassas()
        => Assert.Equal("massas", CategoryMapper.MapToSlug("Italianas"));

    [Fact]
    public void MapToSlug_CornFlakes_ReturnsCereais()
        => Assert.Equal("cereais", CategoryMapper.MapToSlug("Corn Flakes"));

    [Fact]
    public void MapToSlug_AveiaMusliEPreparados_ReturnsCereais()
        => Assert.Equal("cereais", CategoryMapper.MapToSlug("Aveia, Muesli e Preparados"));

    // ── Snacks / Bolos / Pão ───────────────────────────────────────────────

    [Fact]
    public void MapToSlug_Aperitivos_ReturnsBolachas()
        => Assert.Equal("bolachas", CategoryMapper.MapToSlug("Aperitivos"));

    [Fact]
    public void MapToSlug_SementesEPevides_ReturnsBolachas()
        => Assert.Equal("bolachas", CategoryMapper.MapToSlug("Sementes e Pevides"));

    [Fact]
    public void MapToSlug_CrepesPetitGateau_ReturnsBоlosSobremesas()
        => Assert.Equal("bolos-sobremesas", CategoryMapper.MapToSlug("Crepes e Petit Gâteau"));

    [Fact]
    public void MapToSlug_TartesGeladasEViennettas_ReturnsBоlosSobremesas()
        => Assert.Equal("bolos-sobremesas", CategoryMapper.MapToSlug("Tartes Geladas e Viennettas"));

    [Fact]
    public void MapToSlug_Folhados_ReturnsBоlosSobremesas()
        => Assert.Equal("bolos-sobremesas", CategoryMapper.MapToSlug("Folhados"));

    // ── Bebidas Alcoólicas ─────────────────────────────────────────────────

    [Fact]
    public void MapToSlug_Rum_ReturnsBedidasAlcoolicas()
        => Assert.Equal("bebidas-alcoolicas", CategoryMapper.MapToSlug("Rum"));

    [Fact]
    public void MapToSlug_Vodka_ReturnsBedidasAlcoolicas()
        => Assert.Equal("bebidas-alcoolicas", CategoryMapper.MapToSlug("Vodka"));

    [Fact]
    public void MapToSlug_SangriasEAromatizados_ReturnsBedidasAlcoolicas()
        => Assert.Equal("bebidas-alcoolicas", CategoryMapper.MapToSlug("Sangrias e Aromatizados"));

    [Fact]
    public void MapToSlug_Lbv_ReturnsBedidasAlcoolicas()
        => Assert.Equal("bebidas-alcoolicas", CategoryMapper.MapToSlug("LBV"));

    // ── Bebidas Vegetais / Higiene / Limpeza ──────────────────────────────

    [Fact]
    public void MapToSlug_BebidaSoja_ReturnsSumos()
        => Assert.Equal("sumos", CategoryMapper.MapToSlug("Bebida Soja"));

    [Fact]
    public void MapToSlug_BebidaAveia_ReturnsSumos()
        => Assert.Equal("sumos", CategoryMapper.MapToSlug("Bebida Aveia"));

    [Fact]
    public void MapToSlug_GelDeBanho_ReturnsHigienePessoal()
        => Assert.Equal("higiene-pessoal", CategoryMapper.MapToSlug("Gel de Banho"));

    [Fact]
    public void MapToSlug_Toalhitas_ReturnsHigienePessoal()
        => Assert.Equal("higiene-pessoal", CategoryMapper.MapToSlug("Toalhitas"));

    [Fact]
    public void MapToSlug_ArrumacaoEOrganizacao_ReturnsLimpezaLar()
        => Assert.Equal("limpeza-lar", CategoryMapper.MapToSlug("Arrumação e Organização"));

    // ── Saúde e Bem-Estar ──────────────────────────────────────────────────

    [Fact]
    public void MapToSlug_SuplementosEVitaminas_ReturnsSaudeBemEstar()
        => Assert.Equal("saude-bem-estar", CategoryMapper.MapToSlug("Suplementos e Vitaminas"));

    [Fact]
    public void MapToSlug_Multivitaminicos_ReturnsSaudeBemEstar()
        => Assert.Equal("saude-bem-estar", CategoryMapper.MapToSlug("Multivitamínicos"));

    [Fact]
    public void MapToSlug_SaudeEBemEstar_ReturnsSaudeBemEstar()
        => Assert.Equal("saude-bem-estar", CategoryMapper.MapToSlug("Saúde e Bem-Estar"));

    // ── Bebé e Puericultura ────────────────────────────────────────────────

    [Fact]
    public void MapToSlug_CarrinhosDePasseio_ReturnsBebePuericultura()
        => Assert.Equal("bebe-puericultura", CategoryMapper.MapToSlug("Carrinhos de Passeio"));

    [Fact]
    public void MapToSlug_GravidezEPuericultura_ReturnsBebePuericultura()
        => Assert.Equal("bebe-puericultura", CategoryMapper.MapToSlug("Gravidez e Puericultura"));

    [Fact]
    public void MapToSlug_BrinquedosDeBebe_ReturnsBebePuericultura()
        => Assert.Equal("bebe-puericultura", CategoryMapper.MapToSlug("Brinquedos de Bebé"));

    [Fact]
    public void MapToSlug_BiberoesETetinas_ReturnsBebePuericultura()
        => Assert.Equal("bebe-puericultura", CategoryMapper.MapToSlug("Biberões e Tetinas"));

    [Fact]
    public void MapToSlug_Peluches_ReturnsBebePuericultura()
        => Assert.Equal("bebe-puericultura", CategoryMapper.MapToSlug("Peluches"));

    [Fact]
    public void MapToSlug_Babetes_ReturnsBebePuericultura()
        => Assert.Equal("bebe-puericultura", CategoryMapper.MapToSlug("Babetes"));

    [Fact]
    public void MapToSlug_CamasEBercos_ReturnsBebePuericultura()
        => Assert.Equal("bebe-puericultura", CategoryMapper.MapToSlug("Camas, Berços e Colchões"));

    [Fact]
    public void MapToSlug_Crianca_ReturnsBebePuericultura()
        => Assert.Equal("bebe-puericultura", CategoryMapper.MapToSlug("Criança"));
}
