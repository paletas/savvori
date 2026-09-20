using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Savvori.Api.Tests.Infrastructure;

namespace Savvori.Api.Tests;

public class ModelStatusTests : IClassFixture<SavvoriWebApiFactory>
{
    private readonly HttpClient _client;

    public ModelStatusTests(SavvoriWebApiFactory factory) => _client = factory.CreateClient();

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
}
