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

    /// <summary>Human-readable label for what is being paged (chain/category), used in logs by the caller.</summary>
    public string Scope { get; }

    /// <summary>Pages observed so far; 1-based after the first <see cref="Observe"/> call.</summary>
    public int PageNumber { get; private set; }

    /// <summary>Why a <see cref="PagingVerdict.Loop"/> was returned; null for any other verdict.</summary>
    public string? Reason { get; private set; }

    /// <param name="scope">Label of the paging scope.</param>
    /// <param name="pageSize">Page size the caller asks the site for; null when unknown, which disables the short-page rule.</param>
    /// <param name="declaredTotal">Total the site declares for this scope; null when the site does not declare one.</param>
    /// <param name="maxPagesWithoutTotal">Hard cap on the page count, only used when no total is declared.</param>
    public PagingGuard(string scope, int? pageSize, int? declaredTotal, int maxPagesWithoutTotal = 500)
    {
        Scope = scope;
        _pageSize = pageSize;
        _declaredTotal = declaredTotal;
        _maxPagesWithoutTotal = maxPagesWithoutTotal;
    }

    /// <summary>
    /// Record one page of ids and get the verdict for it. <paramref name="requestedOffset"/> is the offset
    /// the caller used for this page. Rules run in this fixed order and the first match wins:
    /// (1) an empty page means the site ended on its own terms;
    /// (2) on any page after the first, the same id list as the previous page means we asked again and got the same thing;
    /// (3) on any page after the first, the first id being the one seen at the very start means the site ignored our offset;
    /// (4) a page whose ids have all been counted before brings nothing new;
    /// (5) a page shorter than the requested size is the last page;
    /// (6) with a declared total, a page requested at or beyond that total is past the end; without one, the page cap applies;
    /// otherwise paging continues.
    /// </summary>
    public PagingVerdict Observe(IReadOnlyList<string> ids, int requestedOffset)
    {
        PageNumber++;

        // (1)
        if (ids.Count == 0)
            return Finish(ids, PagingVerdict.NaturalEnd);

        // (2)
        if (PageNumber > 1 && ids.SequenceEqual(_previous))
            return Finish(ids, PagingVerdict.Loop, "repeated page");

        // (3)
        if (PageNumber > 1 && ids[0] == _firstPageFirstId)
            return Finish(ids, PagingVerdict.Loop, "offset ignored");

        // (4)
        var anyNew = false;
        foreach (var id in ids)
            if (_seen.Add(id))
                anyNew = true;
        if (!anyNew)
            return Finish(ids, PagingVerdict.Loop, "no new products");

        // (5)
        if (_pageSize is int shortPage && ids.Count < shortPage)
            return Finish(ids, PagingVerdict.NaturalEnd);

        // (6)
        if (_declaredTotal is int t)
        {
            if (requestedOffset >= t)
                return Finish(ids, PagingVerdict.Loop, "past declared total");
        }
        else if (_declaredTotal is null && PageNumber >= _maxPagesWithoutTotal)
            return Finish(ids, PagingVerdict.Loop, "page bound");

        return Finish(ids, PagingVerdict.Continue);
    }

    /// <summary>Bookkeeping shared by every verdict: remember this page for rules (2)/(3) and its first id for page 1.</summary>
    private PagingVerdict Finish(IReadOnlyList<string> ids, PagingVerdict verdict, string? reason = null)
    {
        if (verdict == PagingVerdict.Loop)
            Reason = reason;

        _previous = ids.ToList();
        if (PageNumber == 1 && ids.Count > 0)
            _firstPageFirstId = ids[0];

        return verdict;
    }
}
