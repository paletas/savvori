namespace Savvori.WebApi.Scraping;

/// <summary>A snapshot of where a scrape run is, reported by a scraper after a page or a finished key.</summary>
/// <param name="ProductsSeen">Distinct products collected so far (before processing).</param>
/// <param name="Key">The category/term currently being paged.</param>
/// <param name="Offset">Offset/start of the page just read.</param>
/// <param name="KeySiteTotal">Site-declared product total of the current key, when it shows one.</param>
/// <param name="KeysDone">Keys fully paged so far.</param>
/// <param name="KeysTotal">Keys the run will visit, when known up front.</param>
/// <param name="PagesDone">Pages fetched so far in the whole run.</param>
/// <param name="KeyPages">Pages fetched so far in the current key.</param>
/// <param name="KeyPagesTotal">Pages the current key is expected to have (declared total / page step), when known.</param>
/// <param name="DoneKeys">Keys fully paged so far, in order.</param>
public sealed record ScrapeProgress(
    int ProductsSeen, string? Key, int Offset, int? KeySiteTotal, int KeysDone, int? KeysTotal,
    int PagesDone = 0, int KeyPages = 0, int? KeyPagesTotal = null, IReadOnlyList<string>? DoneKeys = null);

/// <summary>
/// Time-based emit gate: the first call and every call after <c>interval</c> pass. The clock is a raw monotonic
/// timestamp with its own frequency; elapsed time is divided out before comparing, so a large timestamp
/// (nanoseconds of host uptime on Linux) can never overflow.
/// </summary>
public sealed class ProgressThrottle(TimeSpan interval, Func<long> timestamp, long ticksPerSecond = TimeSpan.TicksPerSecond)
{
    private long? _last;

    public bool ShouldEmit()
    {
        var now = timestamp();
        if (_last is { } last && (now - last) / (double)ticksPerSecond < interval.TotalSeconds) return false;
        _last = now;
        return true;
    }

    public void Reset() => _last = null;
}
