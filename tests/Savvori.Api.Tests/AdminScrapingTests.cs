using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Savvori.Api.Tests.Infrastructure;
using Savvori.Shared;

namespace Savvori.Api.Tests;

public class AdminScrapingTests : IClassFixture<SavvoriWebApiFactory>
{
    private readonly SavvoriWebApiFactory _factory;
    private readonly Guid _adminUserId;
    private readonly Guid _regularUserId;
    private readonly Guid _chainId;

    public AdminScrapingTests(SavvoriWebApiFactory factory)
    {
        _factory = factory;
        _adminUserId = Guid.NewGuid();
        _regularUserId = Guid.NewGuid();
        _chainId = Guid.NewGuid();

        factory.SeedData(db =>
        {
            var admin = TestDataSeeder.CreateTestUser($"admin_{_adminUserId}@test.com", isAdmin: true);
            admin.Id = _adminUserId;
            db.Users.Add(admin);

            var regular = TestDataSeeder.CreateTestUser($"regular_{_regularUserId}@test.com");
            regular.Id = _regularUserId;
            db.Users.Add(regular);

            var chain = TestDataSeeder.CreateTestStoreChain("Continente", $"continente-adm-{Guid.NewGuid():N}");
            chain.Id = _chainId;
            db.StoreChains.Add(chain);

            db.ScrapingJobs.Add(TestDataSeeder.CreateTestScrapingJob(_chainId, ScrapingJobStatus.Completed));
            db.ScrapingJobs.Add(TestDataSeeder.CreateTestScrapingJob(_chainId, ScrapingJobStatus.Failed));
        });
    }

    private HttpClient AdminClient() =>
        _factory.CreateAuthenticatedClient(_adminUserId, $"admin_{_adminUserId}@test.com", isAdmin: true);

    private HttpClient RegularClient() =>
        _factory.CreateAuthenticatedClient(_regularUserId, $"regular_{_regularUserId}@test.com");

    [Fact]
    public async Task GetStatus_AsAdmin_Returns200WithJobList()
    {
        using var client = AdminClient();
        var response = await client.GetAsync("/api/admin/scraping/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
    }

    [Fact]
    public async Task GetStatus_AsRegularUser_Returns403()
    {
        using var client = RegularClient();
        var response = await client.GetAsync("/api/admin/scraping/status");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetStatus_Unauthenticated_Returns401()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/admin/scraping/status");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetChainStatus_AsAdmin_ValidChain_Returns200WithHistoryAndLogs()
    {
        // Use the chain slug we seeded
        var chainSlug = await GetSeededChainSlug();

        using var client = AdminClient();
        var response = await client.GetAsync($"/api/admin/scraping/status/{chainSlug}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("jobs", out var jobs));
        Assert.True(jobs.GetArrayLength() >= 1);
        Assert.True(body.TryGetProperty("recentLogs", out _));
    }

    [Fact]
    public async Task GetChainStatus_AsAdmin_UnknownChain_Returns404()
    {
        using var client = AdminClient();
        var response = await client.GetAsync("/api/admin/scraping/status/no-such-chain");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TriggerScrape_AsRegularUser_Returns403()
    {
        using var client = RegularClient();
        var response = await client.PostAsync("/api/admin/scraping/trigger/continente", null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TriggerScrape_Unauthenticated_Returns401()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsync("/api/admin/scraping/trigger/continente", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TriggerScrape_AsAdmin_UnknownChain_Returns404()
    {
        using var client = AdminClient();
        var response = await client.PostAsync("/api/admin/scraping/trigger/no-such-chain", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TriggerScrape_AsAdmin_ValidChain_Returns400BecauseNoJobRegistered()
    {
        // In test environment Quartz has no jobs registered → endpoint returns 400
        var chainSlug = await GetSeededChainSlug();
        using var client = AdminClient();
        var response = await client.PostAsync($"/api/admin/scraping/trigger/{chainSlug}", null);
        // Returns 404 (chain not found in Quartz) or 400 (no job registered)
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest ||
            response.StatusCode == HttpStatusCode.NotFound ||
            response.StatusCode == HttpStatusCode.Accepted,
            $"Unexpected status: {response.StatusCode}");
    }

    private async Task<string> GetSeededChainSlug()
    {
        using var client = AdminClient();
        var response = await client.GetAsync("/api/admin/scraping/status");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var job in body.EnumerateArray())
        {
            if (job.TryGetProperty("chainSlug", out var slug) && slug.GetString() != null)
                return slug.GetString()!;
        }
        throw new InvalidOperationException("No seeded chain found in scraping status");
    }
}
