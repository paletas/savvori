using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Savvori.Shared;
using Savvori.WebApi.Services;

namespace Savvori.WebApi.Controllers;

/// <summary>
/// Store chains and location API.
/// </summary>
[ApiController]
[Route("api/stores")]
public class StoresController : ControllerBase
{
    private readonly SavvoriDbContext _db;
    private readonly ILocationService _locationService;

    public StoresController(SavvoriDbContext db, ILocationService locationService)
    {
        _db = db;
        _locationService = locationService;
    }

    /// <summary>
    /// List all store chains (or filter by slug).
    /// GET /api/stores?chain=continente
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetStores(
        [FromQuery] string? chain,
        CancellationToken ct = default)
    {
        var query = _db.StoreChains
            .Include(sc => sc.Locations)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(chain))
            query = query.Where(sc => sc.Slug == chain.ToLower());

        var chains = await query
            .OrderBy(sc => sc.Name)
            .Select(sc => new
            {
                sc.Id,
                sc.Name,
                sc.Slug,
                sc.BaseUrl,
                sc.LogoUrl,
                sc.IsActive,
                LocationCount = sc.Locations.Count(l => l.IsActive)
            })
            .ToListAsync(ct);

        return Ok(chains);
    }

    /// <summary>
    /// List all locations for a store chain.
    /// GET /api/stores/{chainSlug}/locations
    /// </summary>
    [HttpGet("{chainSlug}/locations")]
    public async Task<IActionResult> GetChainLocations(
        string chainSlug,
        CancellationToken ct = default)
    {
        var chain = await _db.StoreChains
            .FirstOrDefaultAsync(sc => sc.Slug == chainSlug.ToLower(), ct);

        if (chain is null) return NotFound();

        var locations = await _db.Stores
            .Where(s => s.StoreChainId == chain.Id && s.IsActive)
            .OrderBy(s => s.City).ThenBy(s => s.Name)
            .Select(s => new
            {
                s.Id,
                s.Name,
                s.Address,
                s.PostalCode,
                s.City,
                s.Latitude,
                s.Longitude
            })
            .ToListAsync(ct);

        return Ok(new { ChainId = chain.Id, ChainSlug = chainSlug, Locations = locations });
    }

    /// <summary>
    /// Find stores near a postal code.
    /// GET /api/stores/nearby?postalCode=1000-001&radiusKm=10
    /// </summary>
    [HttpGet("nearby")]
    public async Task<IActionResult> GetNearbyStores(
        [FromQuery] string postalCode,
        [FromQuery] double radiusKm = 10,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(postalCode))
            return BadRequest("postalCode is required");

        var coord = await _locationService.ResolvePostalCodeAsync(postalCode, ct);
        if (coord is null)
            return BadRequest($"Could not resolve postal code '{postalCode}'");

        var stores = await _db.Stores
            .Include(s => s.StoreChain)
            .Where(s => s.IsActive && s.Latitude != null && s.Longitude != null)
            .ToListAsync(ct);

        var nearby = stores
            .Select(s => new
            {
                s.Id,
                s.Name,
                ChainSlug = s.StoreChain?.Slug,
                ChainName = s.StoreChain?.Name,
                s.Address,
                s.PostalCode,
                s.City,
                s.Latitude,
                s.Longitude,
                DistanceKm = _locationService.CalculateDistanceKm(
                    coord.Latitude, coord.Longitude,
                    s.Latitude!.Value, s.Longitude!.Value)
            })
            .Where(s => s.DistanceKm <= radiusKm)
            .OrderBy(s => s.DistanceKm)
            .ToList();

        return Ok(new
        {
            PostalCode = postalCode,
            RadiusKm = radiusKm,
            UserLatitude = coord.Latitude,
            UserLongitude = coord.Longitude,
            StoreCount = nearby.Count,
            Stores = nearby
        });
    }

    /// <summary>
    /// Resolve a Portuguese postal code to coordinates.
    /// GET /api/stores/geocode?postalCode=1000-001
    /// </summary>
    [HttpGet("geocode")]
    public async Task<IActionResult> Geocode(
        [FromQuery] string postalCode,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(postalCode))
            return BadRequest("postalCode is required");

        var coord = await _locationService.ResolvePostalCodeAsync(postalCode, ct);
        if (coord is null)
            return NotFound($"Could not resolve postal code '{postalCode}'");

        return Ok(new
        {
            PostalCode = postalCode,
            Latitude = coord.Latitude,
            Longitude = coord.Longitude
        });
    }
}
