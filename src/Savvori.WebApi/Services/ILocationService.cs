namespace Savvori.WebApi.Services;

/// <summary>
/// Resolves Portuguese postal codes to geographic coordinates.
/// </summary>
public interface ILocationService
{
    /// <summary>
    /// Converts a Portuguese postal code (e.g., "1000-001") to latitude/longitude.
    /// Returns null if the postal code is not found or the request fails.
    /// </summary>
    Task<GeoCoordinate?> ResolvePostalCodeAsync(string postalCode, CancellationToken ct = default);

    /// <summary>
    /// Calculates the distance in kilometres between two coordinates using the Haversine formula.
    /// </summary>
    double CalculateDistanceKm(double lat1, double lon1, double lat2, double lon2);
}

/// <summary>
/// A geographic coordinate pair.
/// </summary>
public record GeoCoordinate(double Latitude, double Longitude);
