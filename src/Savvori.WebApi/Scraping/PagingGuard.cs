namespace Savvori.WebApi.Scraping;

/// <summary>Verdict for one observed page: keep going, the site ended by itself, or the paging is stuck.</summary>
public enum PagingVerdict
{
    Continue,
    NaturalEnd,
    Loop
}

/// <summary>
/// Per-scope, in-memory judge of a paged listing: it decides whether another page request makes sense.
/// Stateless with respect to the site (no HTTP), so each scraper run builds a fresh instance.
/// </summary>
public sealed class PagingGuard
{
    private readonly int? _pageSize;
    private readonly int? _declaredTotal;
    private readonly int _maxPagesWithoutTotal;
    private readonly HashSet<string> _seen = [];
    private List<string> _previous = [];
    private string? _firstPageFirstId;
    private readonly bool _counterlessRepeatEndsListing;

    /// <summary>Human-readable label for what is being paged (chain/category), used in logs by the caller.</summary>
    public string Scope { get; }

    /// <summary>Pages observed so far; 1-based after the first <see cref="Observe"/> call.</summary>
    public int PageNumber { get; private set; }

    /// <summary>Why a <see cref="PagingVerdict.Loop"/> was returned; null for any other verdict.</summary>
    public string? Reason { get; private set; }

    /// <summary>Why paging stopped, for any stop (natural end or loop); null while paging continues.</summary>
    public string? StopReason { get; private set; }

    /// <param name="scope">Label of the paging scope.</param>
    /// <param name="pageSize">Page size the caller asks the site for; null when unknown, which disables the short-page rule.</param>
    /// <param name="declaredTotal">Total the site declares for this scope; null when the site does not declare one.</param>
    /// <param name="maxPagesWithoutTotal">Hard cap on the page count, only used when no total is declared.</param>
    /// <param name="counterlessRepeatEndsListing">When true and the site declares no total, page 2 repeating page 1 is a natural end (a landing page), not a loop.</param>
    public PagingGuard(string scope, int? pageSize, int? declaredTotal, int maxPagesWithoutTotal = 500, bool counterlessRepeatEndsListing = false)
    {
        Scope = scope;
        _pageSize = pageSize;
        _declaredTotal = declaredTotal;
        _maxPagesWithoutTotal = maxPagesWithoutTotal;
        _counterlessRepeatEndsListing = counterlessRepeatEndsListing;
    }

    /// <summary>
    /// Record one page and get the verdict for it. <paramref name="requestedOffset"/> is the item offset the caller
    /// used for this page; <paramref name="rawCount"/> is how many tiles the site served before parsing.
    /// Rules run in this fixed order and the first match wins:
    /// (1) no tiles served means the site ended on its own terms;
    /// (1b) tiles served but none parsed skips rules 2-5 and goes to rule 6;
    /// (2) on any page after the first, the same id list as the previous page means we asked again and got the same thing
    /// (without a declared total and with the counterless flag, page 2 repeating page 1 is a natural end);
    /// (3) on any page after the first, the first id being the one seen at the very start means the site ignored our offset (same exception);
    /// (4) a page whose ids have all been counted before brings nothing new;
    /// (5) without a declared total, a page with fewer raw tiles than the requested size is the last page;
    /// (6) with a declared total, a page requested at or beyond it is past the end and a page reaching it is the last one;
    /// without a total the page cap applies; otherwise paging continues.
    /// </summary>
    public PagingVerdict Observe(IReadOnlyList<string> ids, int requestedOffset, int rawCount)
    {
        PageNumber++;

        // (1)
        if (rawCount == 0)
            return Finish(ids, PagingVerdict.NaturalEnd, $"empty page at offset {requestedOffset}", requestedOffset);

        if (ids.Count > 0)
        {
            var landing = _counterlessRepeatEndsListing && _declaredTotal is null && PageNumber == 2;
            const string landingText = "landing page: page 2 repeats page 1, no counter";

            // (2)
            if (PageNumber > 1 && ids.SequenceEqual(_previous))
                return landing
                    ? Finish(ids, PagingVerdict.NaturalEnd, landingText, requestedOffset)
                    : Finish(ids, PagingVerdict.Loop, "repeated page", requestedOffset);

            // (3)
            if (PageNumber > 1 && ids[0] == _firstPageFirstId)
                return landing
                    ? Finish(ids, PagingVerdict.NaturalEnd, landingText, requestedOffset)
                    : Finish(ids, PagingVerdict.Loop, "offset ignored", requestedOffset);

            // (4)
            var anyNew = false;
            foreach (var id in ids)
                if (_seen.Add(id))
                    anyNew = true;
            if (!anyNew)
                return Finish(ids, PagingVerdict.Loop, "no new products", requestedOffset);

            // (5)
            if (_declaredTotal is null && _pageSize is int p && rawCount < p)
                return Finish(ids, PagingVerdict.NaturalEnd, $"short page: {rawCount} of {p} tiles at offset {requestedOffset}", requestedOffset);
        }

        // (6)
        if (_declaredTotal is int t)
        {
            if (requestedOffset >= t)
                return Finish(ids, PagingVerdict.Loop, "past declared total", requestedOffset);
            if (requestedOffset + rawCount >= t)
                return Finish(ids, PagingVerdict.NaturalEnd, $"reached declared total {t} at offset {requestedOffset} ({rawCount} tiles)", requestedOffset);
        }
        else if (PageNumber >= _maxPagesWithoutTotal)
            return Finish(ids, PagingVerdict.Loop, "page bound", requestedOffset, $"page bound {_maxPagesWithoutTotal}");

        return Finish(ids, PagingVerdict.Continue, null, requestedOffset);
    }

    /// <summary>Bookkeeping shared by every verdict: remember this page for rules (2)/(3), its first id for page 1, and the stop reason.</summary>
    private PagingVerdict Finish(IReadOnlyList<string> ids, PagingVerdict verdict, string? reason, int requestedOffset, string? loopStopReason = null)
    {
        switch (verdict)
        {
            case PagingVerdict.Loop:
                Reason = reason;
                StopReason = loopStopReason ?? $"{reason} at offset {requestedOffset}";
                break;
            case PagingVerdict.NaturalEnd:
                StopReason = reason;
                break;
            default:
                StopReason = null;
                break;
        }

        _previous = ids.ToList();
        if (PageNumber == 1 && ids.Count > 0)
            _firstPageFirstId = ids[0];

        return verdict;
    }
}
