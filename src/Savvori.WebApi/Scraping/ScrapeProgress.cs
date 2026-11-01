namespace Savvori.WebApi.Scraping;

/// <summary>A snapshot of where a scrape run is, reported by a scraper after a page or a finished key.</summary>
/// <param name="ProductsSeen">Distinct products collected so far (before processing).</param>
/// <param name="Key">The category/term currently being paged.</param>
/// <param name="Offset">Offset/start of the page just read.</param>
/// <param name="KeySiteTotal">Site-declared product total of the current key, when it shows one.</param>
/// <param name="KeysDone">Keys fully paged so far.</param>
/// <param name="KeysTotal">Keys the run will visit, when known up front.</param>
public sealed record ScrapeProgress(
    int ProductsSeen, string? Key, int Offset, int? KeySiteTotal, int KeysDone, int? KeysTotal);

/// <summary>Time-based emit gate: the first call and every call after <c>interval</c> pass.</summary>
public sealed class ProgressThrottle(TimeSpan interval, Func<long> nowTicks)
{
    private long? _last;

    public bool ShouldEmit()
    {
        var now = nowTicks();
        if (_last is { } last && now - last < interval.Ticks) return false;
        _last = now;
        return true;
    }

    public void Reset() => _last = null;
}
