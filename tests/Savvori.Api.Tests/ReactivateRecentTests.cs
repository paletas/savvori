using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Savvori.Api.Tests.Infrastructure;
using Savvori.Shared;
using Savvori.WebApi;

namespace Savvori.Api.Tests;

public class ReactivateRecentTests : IClassFixture<SavvoriWebApiFactory>
{
    private readonly SavvoriWebApiFactory _factory;
    private readonly string _slug = $"react-{Guid.NewGuid():N}";
    private readonly Guid _chainId = Guid.NewGuid();

    public ReactivateRecentTests(SavvoriWebApiFactory factory)
    {
        _factory = factory;
        factory.SeedData(db =>
        {
            var chain = TestDataSeeder.CreateTestStoreChain("React", _slug);
            chain.Id = _chainId;
            db.StoreChains.Add(chain);
            foreach (var (ext, age) in new[] { ("recent-1", 1), ("recent-2", 2), ("old", 10) })
            {
                var sp = TestDataSeeder.CreateTestStoreProduct(_chainId, externalId: ext);
                sp.IsActive = false;
                sp.LastScraped = DateTime.UtcNow.AddDays(-age);
                db.StoreProducts.Add(sp);
            }
        });
    }

    private async Task<JsonElement> CallAsync(bool apply)
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsync($"/api/admin/scraping/reactivate-recent?apply={apply.ToString().ToLowerInvariant()}",
            null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return doc.RootElement.EnumerateArray().Single(e => e.GetProperty("chainSlug").GetString() == _slug).Clone();
    }

    private Dictionary<string, bool> ActiveByExternalId()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SavvoriDbContext>();
        return db.StoreProducts.Where(sp => sp.StoreChainId == _chainId).ToDictionary(sp => sp.ExternalId, sp => sp.IsActive);
    }

    [Fact]
    public async Task DryRun_CountsOnly()
    {
        var row = await CallAsync(apply: false);
        Assert.Equal(2, row.GetProperty("count").GetInt32());
        Assert.False(row.GetProperty("applied").GetBoolean());
        Assert.All(ActiveByExternalId().Values, active => Assert.False(active));
    }

    [Fact]
    public async Task Apply_ReactivatesWithinGraceOnly()
    {
        var row = await CallAsync(apply: true);
        Assert.Equal(2, row.GetProperty("count").GetInt32());
        var state = ActiveByExternalId();
        Assert.True(state["recent-1"]);
        Assert.True(state["recent-2"]);
        Assert.False(state["old"]);
    }
}
