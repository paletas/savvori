using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NSubstitute;
using Savvori.Api.Tests.Infrastructure;
using Savvori.WebApi.Services;

namespace Savvori.Api.Tests;

public class StoresTests : IClassFixture<SavvoriWebApiFactory>
{
    private readonly SavvoriWebApiFactory _factory;
    private readonly HttpClient _client;
    private readonly Guid _chainId;
    private readonly string _chainSlug;

    public StoresTests(SavvoriWebApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();

        var chainId = Guid.NewGuid();
        _chainId = chainId;
        _chainSlug = $"pingo-doce-{Guid.NewGuid():N}";

        factory.SeedData(db =>
        {
            var chain = TestDataSeeder.CreateTestStoreChain("Pingo Doce", _chainSlug);
            chain.Id = chainId;
            db.StoreChains.Add(chain);

            db.Stores.Add(TestDataSeeder.CreateTestStore(chainId, "Pingo Doce Lisboa", 38.716, -9.139));
            db.Stores.Add(TestDataSeeder.CreateTestStore(chainId, "Pingo Doce Porto", 41.157, -8.629));
        });
    }

    [Fact]
    public async Task GetStores_ReturnsAllActiveChains()
    {
        var response = await _client.GetAsync("/api/stores");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.True(body.GetArrayLength() >= 1);
    }

    [Fact]
    public async Task GetStores_WithChainFilter_ReturnsFilteredChain()
    {
        var response = await _client.GetAsync($"/api/stores?chain={_chainSlug}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetArrayLength());
        Assert.Equal(_chainSlug, body[0].GetProperty("slug").GetString());
    }

    [Fact]
    public async Task GetChainLocations_ValidChain_ReturnsLocations()
    {
        var response = await _client.GetAsync($"/api/stores/{_chainSlug}/locations");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(_chainSlug, body.GetProperty("chainSlug").GetString());
        Assert.Equal(2, body.GetProperty("locations").GetArrayLength());
    }

    [Fact]
    public async Task GetChainLocations_UnknownChain_Returns404()
    {
        var response = await _client.GetAsync("/api/stores/unknown-chain/locations");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetNearbyStores_ValidPostalCode_ReturnsSortedByDistance()
    {
        // Default mock returns (38.716, -9.139) for any postal code
        var response = await _client.GetAsync("/api/stores/nearby?postalCode=1000-001&radiusKm=5000");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("1000-001", body.GetProperty("postalCode").GetString());
        Assert.True(body.GetProperty("storeCount").GetInt32() >= 1);

        // Verify sorted by distance
        var stores = body.GetProperty("stores").EnumerateArray().ToList();
        for (int i = 1; i < stores.Count; i++)
        {
            var prev = stores[i - 1].GetProperty("distanceKm").GetDouble();
            var curr = stores[i].GetProperty("distanceKm").GetDouble();
            Assert.True(prev <= curr);
        }
    }

    [Fact]
    public async Task GetNearbyStores_MissingPostalCode_Returns400()
    {
        var response = await _client.GetAsync("/api/stores/nearby");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetNearbyStores_UnresolvablePostalCode_Returns400()
    {
        // Configure mock to return null for this specific postal code
        _factory.LocationService
            .ResolvePostalCodeAsync("0000-000", Arg.Any<CancellationToken>())
            .Returns((GeoCoordinate?)null);

        var response = await _client.GetAsync("/api/stores/nearby?postalCode=0000-000");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Geocode_ValidPostalCode_ReturnsCoordinates()
    {
        var response = await _client.GetAsync("/api/stores/geocode?postalCode=1000-001");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("1000-001", body.GetProperty("postalCode").GetString());
        Assert.True(body.TryGetProperty("latitude", out _));
        Assert.True(body.TryGetProperty("longitude", out _));
    }

    [Fact]
    public async Task Geocode_MissingPostalCode_Returns400()
    {
        var response = await _client.GetAsync("/api/stores/geocode");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Geocode_UnresolvablePostalCode_Returns404OrNotFound()
    {
        // Configure mock to return null for this postal code
        _factory.LocationService
            .ResolvePostalCodeAsync("9999-999", Arg.Any<CancellationToken>())
            .Returns((GeoCoordinate?)null);

        var response = await _client.GetAsync("/api/stores/geocode?postalCode=9999-999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
