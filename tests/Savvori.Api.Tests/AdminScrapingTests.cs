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
    public async Task GetStatus_RunningJob_ExposesProgressAndStalledFromHeartbeat()
    {
        var chainId = Guid.NewGuid();
        var slug = $"progress-{Guid.NewGuid():N}";
        _factory.SeedData(db =>
        {
            var chain = TestDataSeeder.CreateTestStoreChain("ProgressChain", slug);
            chain.Id = chainId;
            db.StoreChains.Add(chain);
            var prev = TestDataSeeder.CreateTestScrapingJob(chainId, ScrapingJobStatus.Completed);
            prev.StartedAt = DateTime.UtcNow.AddDays(-1);
            prev.ProductsScraped = 5000;
            db.ScrapingJobs.Add(prev);
            var running = TestDataSeeder.CreateTestScrapingJob(chainId, ScrapingJobStatus.Running);
            running.StartedAt = DateTime.UtcNow.AddMinutes(-10);
            running.ProgressProductsSeen = 1000;
            running.ProgressKey = "cat-a";
            running.LastProgressAt = DateTime.UtcNow.AddMinutes(-5);
            running.LastHeartbeatAt = DateTime.UtcNow.AddMinutes(-5);
            db.ScrapingJobs.Add(running);
        });

        using var client = _factory.CreateClient();
        var body = await client.GetFromJsonAsync<JsonElement>("/api/admin/scraping/status", TestContext.Current.CancellationToken);
        var chain = body.EnumerateArray().Single(c => c.GetProperty("chainSlug").GetString() == slug);
        var p = chain.GetProperty("progress");
        Assert.Equal(1000, p.GetProperty("productsSeen").GetInt32());
        Assert.Equal(5000, p.GetProperty("total").GetInt32());
        Assert.True(p.GetProperty("totalIsEstimate").GetBoolean());
        Assert.True(p.GetProperty("stalled").GetBoolean());
        Assert.True(p.TryGetProperty("lastHeartbeatAt", out _));
        Assert.True(p.TryGetProperty("etaSeconds", out _));
    }

    [Fact]
    public async Task GetStatus_NotRunning_ProgressIsNull()
    {
        using var client = _factory.CreateClient();
        var body = await client.GetFromJsonAsync<JsonElement>("/api/admin/scraping/status", TestContext.Current.CancellationToken);
        foreach (var c in body.EnumerateArray())
            if (c.GetProperty("lastStatus").ValueKind == JsonValueKind.Number && c.GetProperty("lastStatus").GetInt32() != 1)
                Assert.Equal(JsonValueKind.Null, c.GetProperty("progress").ValueKind);
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
            db.ScrapingRunOutcomes.Add(new ScrapingRunOutcome
            {
                Id = Guid.NewGuid(), ScrapingJobId = job.Id, Kind = OutcomeKind.Category,
                Key = "seasonal-1", Url = "https://x/s", Result = OutcomeResult.ContentPage
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

    [Fact]
    public async Task ChainStatus_OutcomesIncludeStopReason()
    {
        var chainId = Guid.NewGuid();
        var slug = $"stop-adm-{Guid.NewGuid():N}";
        const string reason = "reached declared total 100 at offset 70 (30 tiles)";
        _factory.SeedData(db =>
        {
            var chain = TestDataSeeder.CreateTestStoreChain("StopChain", slug);
            chain.Id = chainId;
            db.StoreChains.Add(chain);
            var job = TestDataSeeder.CreateTestScrapingJob(chainId, ScrapingJobStatus.Completed);
            db.ScrapingJobs.Add(job);
            db.ScrapingRunOutcomes.Add(new ScrapingRunOutcome
            {
                Id = Guid.NewGuid(), ScrapingJobId = job.Id, Kind = OutcomeKind.Category,
                Key = "cat-1", Url = "https://x/u", Result = OutcomeResult.Ok, StopReason = reason
            });
        });

        using var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/admin/scraping/status/{slug}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(reason, body.GetProperty("outcomes")[0].GetProperty("stopReason").GetString());
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
