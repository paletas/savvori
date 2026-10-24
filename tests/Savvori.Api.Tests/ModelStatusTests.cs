using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Savvori.Api.Tests.Infrastructure;
using Savvori.Shared;
using Savvori.WebApi;

namespace Savvori.Api.Tests;

public class ModelStatusTests : IClassFixture<SavvoriWebApiFactory>
{
    private readonly SavvoriWebApiFactory _factory;
    private readonly HttpClient _client;

    public ModelStatusTests(SavvoriWebApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetStatus_WithFlagOff_ReportsDisabledAndClosedBreaker_WithoutCallingTheModel()
    {
        var response = await _client.GetAsync("/api/admin/model/status", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.False(json.GetProperty("enabled").GetBoolean());
        Assert.Equal("Closed", json.GetProperty("breakerState").GetString());
        Assert.Equal(0, json.GetProperty("queueDepth").GetInt32());
        Assert.Equal(0, json.GetProperty("staleEmbeddings").GetInt32());
    }

    [Fact]
    public async Task RequeueDeadLetters_WithNothingDeadLettered_ReturnsZero()
    {
        var response = await _client.PostAsync("/api/admin/model/requeue-dead-letters", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(0, json.GetProperty("requeued").GetInt32());
    }

    [Theory]
    [InlineData("/api/admin/model/scan")]
    [InlineData("/api/admin/model/generate-candidates")]
    public async Task ScanAndGenerate_AreRefused_WhileTheFeatureFlagIsOff(string url)
    {
        var response = await _client.PostAsync(url, null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task GetStatus_ReportsBreakersTaskModelsAndSpend()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SavvoriDbContext>();
            for (var i = 0; i < 2; i++)
                db.ModelCalls.Add(new ModelCall
                {
                    Id = Guid.NewGuid(), At = DateTime.UtcNow, Backend = "openrouter", Task = "pair",
                    Model = "typesafe/jev-1.13", Status = ModelCallStatus.Ok, HttpStatus = 200, CostUsd = 0.0000167m
                });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var response = await _client.GetAsync("/api/admin/model/status", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var breakers = json.GetProperty("breakers");
        Assert.Equal(2, breakers.GetArrayLength());
        Assert.Equal(["gateway", "openrouter"], breakers.EnumerateArray().Select(b => b.GetProperty("backend").GetString()).ToArray());
        Assert.All(breakers.EnumerateArray(), b => Assert.Equal("Closed", b.GetProperty("state").GetString()));
        foreach (var name in new[] { "categoryModel", "pairJudgeModel", "translatorModel", "embeddingModel" })
            Assert.False(string.IsNullOrEmpty(json.GetProperty(name).GetString()));
        Assert.False(json.TryGetProperty("judgeModel", out _));
        Assert.Equal(0.0000334, json.GetProperty("spendTodayUsd").GetDouble(), 1e-9);
        Assert.Equal(0.0000334, json.GetProperty("spend30dUsd").GetDouble(), 1e-9);
        var byTask = json.GetProperty("spendByTask");
        Assert.Equal("pair", byTask[0].GetProperty("task").GetString());
        Assert.Equal(2, byTask[0].GetProperty("calls30d").GetInt32());
    }
}
