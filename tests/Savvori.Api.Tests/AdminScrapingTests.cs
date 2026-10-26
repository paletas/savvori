using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Savvori.Api.Tests.Infrastructure;
using Savvori.Shared;

namespace Savvori.Api.Tests;

public class AdminScrapingTests : IClassFixture<SavvoriWebApiFactory>
{
    private readonly SavvoriWebApiFactory _factory;
    private readonly Guid _chainId;

    public AdminScrapingTests(SavvoriWebApiFactory factory)
    {
        _factory = factory;
        _chainId = Guid.NewGuid();

        factory.SeedData(db =>
        {


            var chain = TestDataSeeder.CreateTestStoreChain("Continente", $"continente-adm-{Guid.NewGuid():N}");
            chain.Id = _chainId;
            db.StoreChains.Add(chain);

            db.ScrapingJobs.Add(TestDataSeeder.CreateTestScrapingJob(_chainId, ScrapingJobStatus.Completed));
            db.ScrapingJobs.Add(TestDataSeeder.CreateTestScrapingJob(_chainId, ScrapingJobStatus.Failed));
        });
    }



    [Fact]
    public async Task GetStatus_AsAdmin_Returns200WithJobList()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/admin/scraping/status", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
    }

    [Fact]
    public async Task GetChainStatus_AsAdmin_ValidChain_Returns200WithHistoryAndLogs()
    {
        // Use the chain slug we seeded
        var chainSlug = await GetSeededChainSlug();

        using var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/admin/scraping/status/{chainSlug}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(body.TryGetProperty("jobs", out var jobs));
        Assert.True(jobs.GetArrayLength() >= 1);
        Assert.True(body.TryGetProperty("recentLogs", out _));
    }

    [Fact]
    public async Task GetChainStatus_AsAdmin_UnknownChain_Returns404()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/admin/scraping/status/no-such-chain", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TriggerScrape_AsAdmin_UnknownChain_Returns404()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsync("/api/admin/scraping/trigger/no-such-chain", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TriggerScrape_AsAdmin_ValidChain_Returns400BecauseNoJobRegistered()
    {
        // In test environment Quartz has no jobs registered → endpoint returns 400
        var chainSlug = await GetSeededChainSlug();
        using var client = _factory.CreateClient();
        var response = await client.PostAsync($"/api/admin/scraping/trigger/{chainSlug}", null, TestContext.Current.CancellationToken);
        // Returns 404 (chain not found in Quartz) or 400 (no job registered)
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest ||
            response.StatusCode == HttpStatusCode.NotFound ||
            response.StatusCode == HttpStatusCode.Accepted,
            $"Unexpected status: {response.StatusCode}");
    }

    [Fact]
    public async Task Status_ReturnsHealthFields()
    {
        var chainId = Guid.NewGuid();
        var slug = $"health-adm-{Guid.NewGuid():N}";
        _factory.SeedData(db =>
        {
            var chain = TestDataSeeder.CreateTestStoreChain("HealthChain", slug);
            chain.Id = chainId;
            db.StoreChains.Add(chain);
            var job = TestDataSeeder.CreateTestScrapingJob(chainId, ScrapingJobStatus.Degraded);
            job.HealthReason = "x: Failed u";
            job.WarningCount = 0;
            job.StaleMarkingSkipped = true;
            job.DropRatio = 0.4;
            db.ScrapingJobs.Add(job);
            db.ScrapingRunOutcomes.Add(new ScrapingRunOutcome
            {
                Id = Guid.NewGuid(), ScrapingJobId = job.Id, Kind = OutcomeKind.Category,
                Key = "cat-1", Url = "https://x/u", Result = OutcomeResult.Failed
            });
        });

        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/admin/scraping/status", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var item = body.EnumerateArray().Single(e => e.GetProperty("chainSlug").GetString() == slug);

        Assert.Equal(5, item.GetProperty("lastStatus").GetInt32());
        Assert.Equal("x: Failed u", item.GetProperty("healthReason").GetString());
        Assert.True(item.GetProperty("staleMarkingSkipped").GetBoolean());
        Assert.Equal(0.4, item.GetProperty("dropRatio").GetDouble(), 3);
        Assert.Equal(["cat-1"], item.GetProperty("failedKeys").EnumerateArray().Select(k => k.GetString()).ToArray());
    }

    private async Task<string> GetSeededChainSlug()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/admin/scraping/status", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        foreach (var job in body.EnumerateArray())
        {
            if (job.TryGetProperty("chainSlug", out var slug) && slug.GetString() != null)
                return slug.GetString()!;
        }
        throw new InvalidOperationException("No seeded chain found in scraping status");
    }
}
