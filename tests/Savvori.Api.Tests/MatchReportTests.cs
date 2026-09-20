using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Savvori.Api.Tests.Infrastructure;
using Savvori.Shared;

namespace Savvori.Api.Tests;

public class MatchReportTests : IClassFixture<SavvoriWebApiFactory>
{
    private readonly SavvoriWebApiFactory _factory;

    public MatchReportTests(SavvoriWebApiFactory factory) => _factory = factory;

    private static int Bucket(JsonElement report, int storeProducts)
    {
        foreach (var b in report.GetProperty("storeProductsPerCanonical").EnumerateArray())
            if (b.GetProperty("storeProducts").GetInt32() == storeProducts)
                return b.GetProperty("canonicals").GetInt32();
        return 0;
    }

    private async Task<JsonElement> GetReportAsync()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/admin/mapping/match-report", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MatchReport_CountsCrossChainCanonicalsHistogramSizeAndEan()
    {
        var before = await GetReportAsync();

        var slug = Guid.NewGuid().ToString("N");
        factory_Seed(slug);

        var after = await GetReportAsync();

        static int Int(JsonElement e, string name) => e.GetProperty(name).GetInt32();

        // 3 canonicals (A: 2 chains, B: 2 store products in 1 chain, C: 1 store product), 5 store products
        Assert.Equal(Int(before, "totalCanonicals") + 3, Int(after, "totalCanonicals"));
        Assert.Equal(Int(before, "totalStoreProducts") + 5, Int(after, "totalStoreProducts"));
        Assert.Equal(Int(before, "canonicalsWithMultipleChains") + 1, Int(after, "canonicalsWithMultipleChains"));
        Assert.Equal(Bucket(before, 2) + 2, Bucket(after, 2));
        Assert.Equal(Bucket(before, 1) + 1, Bucket(after, 1));
        // A has no size, B and C have one; only C has an EAN; two store products carry an EAN
        Assert.Equal(Int(before, "canonicalsWithNoSize") + 1, Int(after, "canonicalsWithNoSize"));
        Assert.Equal(Int(before, "canonicalsWithEan") + 1, Int(after, "canonicalsWithEan"));
        Assert.Equal(Int(before, "storeProductsWithEan") + 2, Int(after, "storeProductsWithEan"));

        var methods = after.GetProperty("byMatchMethod").EnumerateArray()
            .ToDictionary(m => m.GetProperty("method").GetString()!, m => m.GetProperty("count").GetInt32());
        Assert.True(methods.GetValueOrDefault("report-test") >= 5);
    }

    private void factory_Seed(string slug)
    {
        _factory.SeedData(db =>
        {
            var c1 = TestDataSeeder.CreateTestStoreChain("Chain One", $"one-{slug}");
            var c2 = TestDataSeeder.CreateTestStoreChain("Chain Two", $"two-{slug}");
            db.StoreChains.AddRange(c1, c2);

            var a = TestDataSeeder.CreateTestProduct("A cross-chain");
            var b = TestDataSeeder.CreateTestProduct("B one chain");
            var c = TestDataSeeder.CreateTestProduct("C single");
            b.SizeValue = 1m;
            c.SizeValue = 1m;
            c.EAN = $"56{slug[..11]}";
            db.Products.AddRange(a, b, c);

            void Link(Guid chainId, Product canonical, string? ean = null)
            {
                var sp = TestDataSeeder.CreateTestStoreProduct(chainId, canonical.Id);
                sp.MatchMethod = "report-test";
                sp.EAN = ean;
                db.StoreProducts.Add(sp);
            }

            Link(c1.Id, a, ean: "5600000000000");
            Link(c2.Id, a);
            Link(c1.Id, b);
            Link(c1.Id, b);
            Link(c2.Id, c, ean: c.EAN);
        });
    }
}
