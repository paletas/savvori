using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Savvori.Api.Tests.Infrastructure;
using Savvori.Shared;
using Savvori.WebApi;

namespace Savvori.Api.Tests;

public class RecomputeSizesTests : IClassFixture<SavvoriWebApiFactory>
{
    private readonly SavvoriWebApiFactory _factory;
    private readonly string _chainSlug = $"recompute-{Guid.NewGuid():N}";
    private readonly Guid _chainId;
    private readonly Guid _decimalCommaId = Guid.NewGuid();
    private readonly Guid _unitPriceWinsId = Guid.NewGuid();
    private readonly Guid _noSizeId = Guid.NewGuid();

    public RecomputeSizesTests(SavvoriWebApiFactory factory)
    {
        _factory = factory;
        var chain = TestDataSeeder.CreateTestStoreChain("Recompute", _chainSlug);
        _chainId = chain.Id;

        factory.SeedData(db =>
        {
            db.StoreChains.Add(chain);

            // Previously mis-parsed "0,5L" as 5 L
            AddStoreProduct(db, chain.Id, _decimalCommaId, "Coca-Cola Zero 0,5L", 5m, ProductUnit.L, 1.19m, null);
            // Name says nothing useful for size; stored size disagrees with unit price (€1.19 at €2.38/L = 0.5 L)
            AddStoreProduct(db, chain.Id, _unitPriceWinsId, "Refrigerante Cola 500 ml", 5m, ProductUnit.L, 1.19m, 2.38m);
            // No size anywhere: unchanged
            AddStoreProduct(db, chain.Id, _noSizeId, "Produto sem tamanho", null, ProductUnit.Unit, 1m, null);
        });
    }

    private static void AddStoreProduct(
        SavvoriDbContext db, Guid chainId, Guid id, string name,
        decimal? size, ProductUnit unit, decimal price, decimal? unitPrice)
    {
        var canonical = TestDataSeeder.CreateTestProduct(name);
        canonical.SizeValue = size;
        canonical.Unit = unit;
        db.Products.Add(canonical);

        var sp = TestDataSeeder.CreateTestStoreProduct(chainId, canonical.Id);
        sp.Id = id;
        sp.Name = name;
        sp.SizeValue = size;
        sp.Unit = unit;
        db.StoreProducts.Add(sp);

        var latest = TestDataSeeder.CreateTestStoreProductPrice(id, price);
        latest.UnitPrice = unitPrice;
        db.StoreProductPrices.Add(latest);
    }

    private (decimal? Size, ProductUnit Unit, decimal? CanonicalSize) Read(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SavvoriDbContext>();
        var sp = db.StoreProducts.Single(s => s.Id == id);
        var canonical = db.Products.Single(p => p.Id == sp.CanonicalProductId);
        return (sp.SizeValue, sp.Unit, canonical.SizeValue);
    }

    [Fact]
    public async Task RecomputeSizes_DryRun_ReportsButDoesNotWrite()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync(
            $"/api/admin/mapping/recompute-sizes?chainSlug={_chainSlug}&dryRun=true", null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(2, body.GetProperty("changed").GetInt32());
        Assert.Equal(5m, Read(_decimalCommaId).Size);
    }

    [Fact]
    public async Task RecomputeSizes_FixesDecimalCommaAndUnitPriceDisagreements()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync(
            $"/api/admin/mapping/recompute-sizes?chainSlug={_chainSlug}", null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(3, body.GetProperty("total").GetInt32());

        var comma = Read(_decimalCommaId);
        Assert.Equal(0.5m, comma.Size);
        Assert.Equal(ProductUnit.L, comma.Unit);
        Assert.Equal(0.5m, comma.CanonicalSize);

        // "500 ml" in the name → 500 Ml, consistent with the unit price
        var ml = Read(_unitPriceWinsId);
        Assert.Equal(500m, ml.Size);
        Assert.Equal(ProductUnit.Ml, ml.Unit);

        Assert.Null(Read(_noSizeId).Size);
    }

    [Fact]
    public async Task RecomputeSizes_KeepsStoredStructuredSize_WhenNameParseContradictsIt()
    {
        var keptId = Guid.NewGuid();
        var noUnitPriceId = Guid.NewGuid();
        _factory.SeedData(db =>
        {
            // Structured 500 g agrees with €1.99 at €3.98/kg; the name's "10 un" must not replace it
            AddStoreProduct(db, _chainId, keptId, "Queijo Flamengo Fatiado 10 un", 500m, ProductUnit.G, 1.99m, 3.98m);
            // No unit price to arbitrate and not a decimal-comma slip: keep the stored size too
            AddStoreProduct(db, _chainId, noUnitPriceId, "Queijo Fatiado 10 un", 500m, ProductUnit.G, 1.99m, null);
        });

        using var client = _factory.CreateClient();
        var resp = await client.PostAsync(
            $"/api/admin/mapping/recompute-sizes?chainSlug={_chainSlug}", null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var kept = Read(keptId);
        Assert.Equal(500m, kept.Size);
        Assert.Equal(ProductUnit.G, kept.Unit);
        var noUnitPrice = Read(noUnitPriceId);
        Assert.Equal(500m, noUnitPrice.Size);
        Assert.Equal(ProductUnit.G, noUnitPrice.Unit);
    }

    [Fact]
    public async Task RecomputeSizes_InvalidChain_Returns404()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync(
            "/api/admin/mapping/recompute-sizes?chainSlug=nonexistent-chain-xyz", null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
