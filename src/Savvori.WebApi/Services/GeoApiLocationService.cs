using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;

namespace Savvori.WebApi.Services;

/// <summary>
/// Implements <see cref="ILocationService"/> using the free geoapi.pt service
/// (https://geo.iotech.pt/cp/{postalCode}?json=1) for postal-code resolution.
/// Results are cached in-memory for 24 hours to minimise outbound requests.
/// </summary>
public sealed class GeoApiLocationService : ILocationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<GeoApiLocationService> _logger;

    private const double EarthRadiusKm = 6371.0;

    public GeoApiLocationService(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        ILogger<GeoApiLocationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    public async Task<GeoCoordinate?> ResolvePostalCodeAsync(string postalCode, CancellationToken ct = default)
    {
        var normalised = NormalisePostalCode(postalCode);
        if (string.IsNullOrEmpty(normalised)) return null;

        var cacheKey = $"geo:{normalised}";
        if (_cache.TryGetValue(cacheKey, out GeoCoordinate? cached))
            return cached;

        try
        {
            var client = _httpClientFactory.CreateClient("geoapi");
            var response = await client.GetAsync($"/cp/{normalised}?json=1", ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("GeoAPI returned {Status} for postal code {Code}", response.StatusCode, normalised);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<GeoApiResponse>(content, JsonOptions);

            if (result?.Latitude is null || result.Longitude is null) return null;

            if (!double.TryParse(result.Latitude, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var lat)) return null;
            if (!double.TryParse(result.Longitude, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var lon)) return null;

            var coord = new GeoCoordinate(lat, lon);
            _cache.Set(cacheKey, coord, TimeSpan.FromHours(24));
            return coord;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve postal code {Code}", normalised);
            return null;
        }
    }

    public double CalculateDistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);

        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2))
              * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return EarthRadiusKm * c;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180;

    private static string NormalisePostalCode(string input)
    {
        // Accept "XXXX-XXX" or "XXXXXXX"
        var digits = new string(input.Where(char.IsDigit).ToArray());
        return digits.Length >= 7 ? $"{digits[..4]}-{digits[4..7]}" : string.Empty;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class GeoApiResponse
    {
        [JsonPropertyName("lat")]
        public string? Latitude { get; set; }

        [JsonPropertyName("lng")]
        public string? Longitude { get; set; }

        [JsonPropertyName("localidade")]
        public string? Locality { get; set; }

        [JsonPropertyName("concelho")]
        public string? Municipality { get; set; }

        [JsonPropertyName("distrito")]
        public string? District { get; set; }
    }
}
