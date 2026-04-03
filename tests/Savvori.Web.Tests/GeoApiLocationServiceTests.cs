using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Savvori.WebApi.Services;

namespace Savvori.Web.Tests;

/// <summary>
/// Unit tests for GeoApiLocationService — postal code resolution and Haversine distance.
/// </summary>
public class GeoApiLocationServiceTests
{
    private static GeoApiLocationService CreateService(
        HttpMessageHandler? handler = null,
        IMemoryCache? cache = null)
    {
        handler ??= new FakeHttpMessageHandler();
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("geoapi").Returns(new HttpClient(handler) { BaseAddress = new Uri("https://geo.iotech.pt") });
        cache ??= new MemoryCache(new MemoryCacheOptions());
        var logger = Substitute.For<ILogger<GeoApiLocationService>>();
        return new GeoApiLocationService(factory, cache, logger);
    }

    private static FakeHttpMessageHandler MakeGeoHandler(string lat, string lng, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new FakeHttpMessageHandler();
        var json = JsonSerializer.Serialize(new { lat, lng, localidade = "Lisboa", concelho = "Lisboa", distrito = "Lisboa" });
        handler.SetupRoute("cp/", json, status);
        return handler;
    }

    // ─── ResolvePostalCodeAsync ────────────────────────────────────────────────

    [Fact]
    public async Task ResolvePostalCode_ValidCode_ReturnsCoordinates()
    {
        var service = CreateService(MakeGeoHandler("38.7167", "-9.1333"));
        var coord = await service.ResolvePostalCodeAsync("1000-001", CancellationToken.None);

        Assert.NotNull(coord);
        Assert.Equal(38.7167, coord.Latitude, precision: 4);
        Assert.Equal(-9.1333, coord.Longitude, precision: 4);
    }

    [Fact]
    public async Task ResolvePostalCode_AcceptsCodeWithoutHyphen()
    {
        var service = CreateService(MakeGeoHandler("38.7167", "-9.1333"));
        var coord = await service.ResolvePostalCodeAsync("1000001", CancellationToken.None);

        Assert.NotNull(coord);
    }

    [Fact]
    public async Task ResolvePostalCode_NormalisesHyphenatedCode()
    {
        // "1000-001" and "1000001" should resolve to same cache key
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(MakeGeoHandler("38.7167", "-9.1333"), cache);

        var coord1 = await service.ResolvePostalCodeAsync("1000-001", CancellationToken.None);
        var coord2 = await service.ResolvePostalCodeAsync("1000001", CancellationToken.None);

        Assert.NotNull(coord1);
        Assert.NotNull(coord2);
    }

    [Fact]
    public async Task ResolvePostalCode_InvalidCode_ReturnsNull()
    {
        var service = CreateService();
        var coord = await service.ResolvePostalCodeAsync("INVALID", CancellationToken.None);
        Assert.Null(coord);
    }

    [Fact]
    public async Task ResolvePostalCode_TooShortDigits_ReturnsNull()
    {
        var service = CreateService();
        var coord = await service.ResolvePostalCodeAsync("12345", CancellationToken.None);
        Assert.Null(coord);
    }

    [Fact]
    public async Task ResolvePostalCode_ApiReturnsError_ReturnsNull()
    {
        var service = CreateService(MakeGeoHandler("", "", HttpStatusCode.NotFound));
        var coord = await service.ResolvePostalCodeAsync("1000-001", CancellationToken.None);
        Assert.Null(coord);
    }

    [Fact]
    public async Task ResolvePostalCode_CachesResult_SecondCallUsesCache()
    {
        var handler = MakeGeoHandler("38.7167", "-9.1333");
        var service = CreateService(handler);

        var coord1 = await service.ResolvePostalCodeAsync("1000-001", CancellationToken.None);
        var coord2 = await service.ResolvePostalCodeAsync("1000-001", CancellationToken.None);

        Assert.NotNull(coord1);
        Assert.NotNull(coord2);
        // Only 1 HTTP call; 2nd comes from cache
        Assert.Equal(1, handler.RequestCount);
    }

    // ─── CalculateDistanceKm ───────────────────────────────────────────────────

    [Fact]
    public void CalculateDistance_SamePoint_ReturnsZero()
    {
        var service = CreateService();
        var dist = service.CalculateDistanceKm(38.7, -9.1, 38.7, -9.1);
        Assert.Equal(0.0, dist, precision: 5);
    }

    [Fact]
    public void CalculateDistance_LisbonToPorto_ReturnsApproxCorrectKm()
    {
        // Lisbon: 38.7169° N, 9.1395° W
        // Porto: 41.1579° N, 8.6291° W
        // Expected: ~275 km (straight line)
        var service = CreateService();
        var dist = service.CalculateDistanceKm(38.7169, -9.1395, 41.1579, -8.6291);
        Assert.True(dist > 270 && dist < 285, $"Expected ~275km, got {dist:F1}km");
    }

    [Fact]
    public void CalculateDistance_IsSymmetric()
    {
        var service = CreateService();
        var d1 = service.CalculateDistanceKm(38.7, -9.1, 41.1, -8.6);
        var d2 = service.CalculateDistanceKm(41.1, -8.6, 38.7, -9.1);
        Assert.Equal(d1, d2, precision: 6);
    }

    [Fact]
    public void CalculateDistance_ShortDistance_ReturnsReasonableValue()
    {
        // Two points ~1km apart in Lisbon
        var service = CreateService();
        var dist = service.CalculateDistanceKm(38.7169, -9.1395, 38.7238, -9.1395);
        Assert.True(dist > 0.5 && dist < 2.0, $"Expected ~0.7km, got {dist:F2}km");
    }
}
