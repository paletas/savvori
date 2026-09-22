using Savvori.WebApi.Modeling;

namespace Savvori.Web.Tests.Modeling;

/// <summary>
/// The pairs come from real prod suggestions at 100% confidence that were still wrong (rejected by hand,
/// 2026-09-22/23), so a change that breaks one of them is a real regression.
/// </summary>
public sealed class CategoryGuardTests
{
    [Theory]
    [InlineData("MÁQUINA DE COZER ARROZ QILIVE Q.5130 3.7 L 700 W COM CESTO VAPORIZADOR", "Arroz")]
    [InlineData("CORTADOR MASSA ACTUEL", "Massas")]
    [InlineData("MASSA DE POLIR BISNAGA REDEX 125ML", "Massas")]
    [InlineData("COLHER SERVIR ARROZ JOMAFE INOX", "Arroz")]
    [InlineData("DISPENSADOR BEBIDA MPS MAYA S 0.5L", "Bebidas Energéticas e Desportivas")]
    [InlineData("Escova de Dentes Criança Dentes de Leite Aquafresh 1un", "Leite")]
    [InlineData("Intercomunicador Digital com Câmara Philips Avent", "Leite")]
    [InlineData("SOLUÇÃO MAGNESIA PHILIPS LEITE 83MG/ML 200ML", "Leite")]
    [InlineData("Banco, Mesa e Baú Stitch Disney", "Limpeza do Lar")]
    [InlineData("CONJUNTO DE TÁBUA PARA QUEIJO ACÁCIA 28X15X1CM", "Queijos")]
    [InlineData("ALMOFADA COM QUEIJO A VACA QUE RI 100 G", "Queijos")]
    [InlineData("Yarrah Cao Pate Frango Algas Bio 150G", "Queijos")]
    // found mining the 0.85-1.0 confidence band via the bulk/preview guard-impact report (prod, 2026-09-23)
    [InlineData("Champô Urtiga Bio", "Chá e Infusões")]
    // "congelado" (frozen) vs "gelado" (ice cream): a very common miss across today's whole review (prod, 2026-09-23)
    [InlineData("Tentáculos de Polvo Congelados Continente", "Gelados")]
    [InlineData("Polvo Limpo Nacional Ultracongelado", "Gelados")]
    [InlineData("Chocos Pequenos com Tinta Congelados", "Gelados")]
    public void LiteralWordCollisions_AreFlagged(string name, string category) =>
        Assert.True(CategoryGuard.Suspicious(name, category));

    [Theory]
    // real products that were correctly suggested and should never be blocked
    [InlineData("Leite Meio Gordo Bio Prado Verde", "Leite")]
    [InlineData("QUEIJO AUCHAN FLAMENGO BIO FATIAS 150G", "Queijos")]
    [InlineData("Arroz Basmati", "Arroz")]
    [InlineData("MASSAS HELICES MILANEZA TRICOLORES ESPECIAL SALADA 500G", "Massas")]
    // the same "leite" word, correctly meaning lotion, correctly filed as a cosmetic - never a false positive
    [InlineData("LEITE SOLAR MUSTELA ROSTO SPF50+ 40ML", "Proteção Solar")]
    [InlineData("APÓS SOL AMBRE SOLAIRE LEITE HIDRATANTE 400ML", "Proteção Solar")]
    // real pet food, correctly filed as pet food
    [InlineData("RAÇÃO PARA GATINHOS PRO PLAN COM FRANGO E ARROZ 400G", "Comida para Gatos")]
    [InlineData("COMIDA HÚMIDA MARTIN SELLIER CÃO PEIXE 400G", "Comida para Cães")]
    // a real baby-gear object, correctly filed under baby gear
    [InlineData("Espelho de Segurança Auto 360° Asalvo", "Puericultura e Mobiliário Bebé")]
    // a real shampoo, correctly filed under hair care
    [InlineData("Champô Alperce Criança Garnier Ultra Suave", "Cabelo")]
    // a real children's book, correctly filed under books
    [InlineData("100 Primeiros - Números, Cores e Formas de Roger Priddy", "Papelaria e Livros")]
    // a real ice cream, correctly filed under Gelados; a real frozen vegetable, correctly filed under its own frozen category
    [InlineData("Gelado Cornetto Mini Clássico", "Gelados")]
    [InlineData("Ervilhas Ultracongeladas Bio", "Legumes Congelados")]
    public void RealMatches_AreNeverFlagged(string name, string category) =>
        Assert.False(CategoryGuard.Suspicious(name, category));
}
