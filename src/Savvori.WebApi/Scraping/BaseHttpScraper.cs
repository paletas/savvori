using System.Diagnostics;
using System.Net;
using System.Text.Json;
using AngleSharp;
using AngleSharp.Dom;
using Polly;
using Savvori.Shared;
using Polly.Retry;
using Polly.Timeout;

namespace Savvori.WebApi.Scraping;

/// <summary>
/// Abstract base for all store scrapers. Provides rate-limited, retrying HTTP helpers: requests are
/// serially spaced, bounded by a request ceiling and a wall-clock limit, and a 403/429/bot marker halts the run.
/// </summary>
public abstract class BaseHttpScraper : IStoreScraper
{
    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline;
    private readonly SemaphoreSlim _throttle;

    protected readonly HttpClient Http;
    protected readonly ILogger Logger;

    protected BaseHttpScraper(
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        string httpClientName,
        int maxConcurrentRequests = 2,
        TimeSpan? retryBaseDelay = null)
    {
        Http = httpClientFactory.CreateClient(httpClientName);
        Logger = logger;
        _throttle = new SemaphoreSlim(maxConcurrentRequests, maxConcurrentRequests);

        var delay = retryBaseDelay ?? TimeSpan.FromSeconds(2);
        _pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                Delay = delay,
                ShouldHandle = async args =>
                {
                    if (args.Outcome.Exception is HttpRequestException or TimeoutRejectedException) return true;
                    var r = args.Outcome.Result;
                    if (r is null) return false;
                    if (r.StatusCode == HttpStatusCode.ServiceUnavailable ||
                        r.StatusCode == HttpStatusCode.RequestTimeout ||
                        (int)r.StatusCode >= 500)
                        return true;
                    if (r.StatusCode == HttpStatusCode.OK && r.Content is not null)
                    {
                        await r.Content.LoadIntoBufferAsync();
                        return r.Content.Headers.ContentLength == 0;
                    }
                    return false;
                },
                OnRetry = args =>
                {
                    Logger.LogWarning("Retry {Attempt} after {Delay}ms for {StoreChainSlug}",
                        args.AttemptNumber, args.RetryDelay.TotalMilliseconds, StoreChainSlug);
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(TimeSpan.FromSeconds(30))
            .Build();
    }

    public abstract string StoreChainSlug { get; }

    /// <summary>Minimum gap between any two request attempts of this scraper (retries included).</summary>
    public TimeSpan RequestSpacing { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Maximum bounded request attempts per run; reaching it halts the run without sending more.</summary>
    public int RequestCeiling { get; set; } = int.MaxValue;

    /// <summary>Maximum wall-clock time per run (measured from <see cref="BeginRun"/>).</summary>
    public TimeSpan WallClockLimit { get; set; } = TimeSpan.FromHours(4);

    protected int RequestsUsed { get; private set; }
    protected bool Halted { get; private set; }
    protected bool Incomplete { get; set; }
    protected string? HaltReason { get; private set; }

    /// <summary>Receives throttled progress snapshots; set by the job runner. Sink errors never fail a scrape.</summary>
    public Action<ScrapeProgress>? ProgressSink { get; set; }

    /// <summary>Minimum gap between page-level progress snapshots (key completions always pass).</summary>
    public TimeSpan ProgressInterval { get; set; } = TimeSpan.FromSeconds(15);

    private ProgressThrottle? _progressThrottle;
    private TimeSpan _throttleInterval;
    private int _keysDone;
    private int? _keysTotal;
    private int _pagesDone;
    private int _keyPages;
    private string? _progressKey;
    private readonly List<string> _doneKeys = [];

    private readonly Stopwatch _runClock = new();
    private readonly SemaphoreSlim _spacingGate = new(1, 1);
    private long _lastSend; // 0 = none yet
    private string? _boundWarning;

    private sealed class BoundReachedException : Exception;

    /// <summary>Resets halt state, counters and the wall clock at the start of a run.</summary>
    protected void BeginRun()
    {
        Halted = Incomplete = false;
        HaltReason = null;
        _boundWarning = null;
        RequestsUsed = 0;
        _keysDone = 0;
        _keysTotal = null;
        _pagesDone = 0;
        _keyPages = 0;
        _progressKey = null;
        _doneKeys.Clear();
        _progressThrottle = null;
        _runClock.Restart();
    }

    /// <summary>Declares how many keys (categories/sections) the run will visit.</summary>
    protected void SetKeysTotal(int keysTotal) => _keysTotal = keysTotal;

    /// <summary>
    /// Reports progress after a page; throttled to <see cref="ProgressInterval"/>. Call once per fetched page;
    /// <paramref name="pageStep"/> (products per page) lets the declared total become an expected page count.
    /// </summary>
    protected void ReportProgress(string key, int offset, int productsSeen, int? keySiteTotal, int? pageStep = null)
    {
        if (key != _progressKey) { _progressKey = key; _keyPages = 0; }
        _pagesDone++;
        _keyPages++;
        int? keyPagesTotal = keySiteTotal is > 0 && pageStep is > 0 ? (keySiteTotal.Value + pageStep.Value - 1) / pageStep.Value : null;
        Emit(new ScrapeProgress(productsSeen, key, offset, keySiteTotal, _keysDone, _keysTotal,
            _pagesDone, _keyPages, keyPagesTotal, _doneKeys.ToArray()), force: false);
    }

    /// <summary>Counts a finished key and reports immediately.</summary>
    protected void ReportKeyDone(string key, int productsSeen)
    {
        _keysDone++;
        _doneKeys.Add(key);
        if (key != _progressKey) { _progressKey = key; _keyPages = 0; }
        Emit(new ScrapeProgress(productsSeen, key, 0, null, _keysDone, _keysTotal,
            _pagesDone, _keyPages, null, _doneKeys.ToArray()), force: true);
    }

    private void Emit(ScrapeProgress progress, bool force)
    {
        var sink = ProgressSink;
        if (sink is null) return;
        try
        {
            if (_progressThrottle is null || _throttleInterval != ProgressInterval)
            {
                _throttleInterval = ProgressInterval;
                // Raw timestamp + its frequency: multiplying a Linux ns timestamp by TicksPerSecond overflows a long.
                _progressThrottle = new ProgressThrottle(ProgressInterval, Stopwatch.GetTimestamp, Stopwatch.Frequency);
            }
            if (!_progressThrottle.ShouldEmit() && !force) return;
            sink(progress);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Progress sink failed for {StoreChainSlug}; ignored", StoreChainSlug);
        }
    }

    private async Task EnterAttemptAsync(bool bounded, CancellationToken ct)
    {
        await _spacingGate.WaitAsync(ct);
        try
        {
            if (_lastSend != 0)
            {
                var wait = RequestSpacing - Stopwatch.GetElapsedTime(_lastSend);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            }
            if (bounded)
            {
                if (RequestsUsed >= RequestCeiling) Bound($"request ceiling {RequestCeiling}");
                else if (_runClock.Elapsed >= WallClockLimit) Bound($"wall clock {WallClockLimit.TotalHours:0.##}h");
                RequestsUsed++;
            }
            _lastSend = Stopwatch.GetTimestamp();
        }
        finally
        {
            _spacingGate.Release();
        }

        void Bound(string what)
        {
            Halted = Incomplete = true;
            HaltReason = what;
            _boundWarning = $"Bound reached: {what} after {RequestsUsed} requests; run ended early";
            throw new BoundReachedException();
        }
    }

    /// <summary>Pause before the end-of-run re-attempt of failed targets.</summary>
    public TimeSpan ReattemptDelay { get; set; } = TimeSpan.FromSeconds(5);

    public abstract Task<ScrapeRunResult> ScrapeProductsAsync(
        string? category = null, CancellationToken ct = default);

    public virtual Task<CanaryResult> ProbeAsync(CancellationToken ct = default)
        => Task.FromResult(new CanaryResult(true, "not implemented", 0));

    public abstract Task<IReadOnlyList<ScrapedStoreLocation>> ScrapeStoreLocationsAsync(
        CancellationToken ct = default);

    protected async Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default)
    {
        await _throttle.WaitAsync(ct);
        try
        {
            return await _pipeline.ExecuteAsync(async token =>
            {
                await EnterAttemptAsync(false, token);
                var response = await Http.GetAsync(url, token);
                return response;
            }, ct);
        }
        finally
        {
            _throttle.Release();
        }
    }

    private async Task<HttpResponseMessage> GetAsync(string url, ScrapeOutcome outcome, CancellationToken ct)
    {
        await _throttle.WaitAsync(ct);
        try
        {
            var attempts = 0;
            return await _pipeline.ExecuteAsync(async token =>
            {
                attempts++;
                await EnterAttemptAsync(true, token);
                outcome.Retries = attempts - 1;
                var response = await Http.GetAsync(url, token);
                var code = (int)response.StatusCode;
                outcome.StatusCounts[code] = outcome.StatusCounts.GetValueOrDefault(code) + 1;
                return response;
            }, ct);
        }
        finally
        {
            _throttle.Release();
        }
    }

    private static readonly string[] BotMarkers =
        ["captcha", "cf-challenge", "Just a moment", "Access Denied", "px-captcha"];

    private async Task<(HttpResponseMessage? Resp, string? Body)> FetchAsync(
        string url, ScrapeOutcome outcome, bool checkBotMarkers, CancellationToken ct)
    {
        void Fail(OutcomeResult result, string error)
        {
            if (outcome.Result == OutcomeResult.Ok) outcome.Result = result;
            outcome.FirstError ??= error;
            outcome.StopReason ??= result == OutcomeResult.Blocked ? $"blocked {error}" : $"failed: {error}";
        }

        if (Halted) return (null, null);

        HttpResponseMessage resp;
        try
        {
            resp = await GetAsync(url, outcome, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (BoundReachedException)
        {
            outcome.StopReason ??= HaltReason;
            return (null, null);
        }
        catch (Exception ex)
        {
            Fail(OutcomeResult.Failed, ex.Message);
            return (null, null);
        }

        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            var why = $"HTTP {(int)resp.StatusCode}";
            Fail(OutcomeResult.Blocked, why);
            Halted = Incomplete = true;
            HaltReason = why;
            return (null, null);
        }

        if (!resp.IsSuccessStatusCode)
        {
            Fail(OutcomeResult.Failed, $"HTTP {(int)resp.StatusCode}");
            return (null, null);
        }

        var finalUri = resp.RequestMessage?.RequestUri;
        if (finalUri != null)
        {
            var expected = new Uri(url, UriKind.RelativeOrAbsolute);
            var expectedPath = expected.IsAbsoluteUri ? expected.AbsolutePath : url.Split('?', '#')[0];
            if (!string.Equals(finalUri.AbsolutePath.TrimEnd('/'), expectedPath.TrimEnd('/'),
                    StringComparison.OrdinalIgnoreCase))
            {
                Fail(OutcomeResult.Redirected, "Redirected to " + finalUri);
                return (null, null);
            }
        }

        string body;
        try
        {
            body = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Fail(OutcomeResult.Failed, ex.Message);
            return (null, null);
        }

        if (body.Length == 0)
        {
            Fail(OutcomeResult.Failed, "Empty response body");
            return (null, null);
        }

        if (checkBotMarkers &&BotMarkers.Any(m => body.Contains(m, StringComparison.OrdinalIgnoreCase)))
        {
            Fail(OutcomeResult.Blocked, "Bot-protection marker in response");
            Halted = Incomplete = true;
            HaltReason = "bot protection marker";
            return (null, null);
        }

        outcome.PagesFetched++;
        return (resp, body);
    }

    protected async Task<string?> GetStringAsync(string url, ScrapeOutcome outcome, CancellationToken ct)
        => (await FetchAsync(url, outcome, true, ct)).Body;

    protected async Task<IDocument?> GetHtmlAsync(string url, ScrapeOutcome outcome, CancellationToken ct)
    {
        var (_, body) = await FetchAsync(url, outcome, true, ct);
        if (body is null) return null;
        var context = BrowsingContext.New(Configuration.Default);
        return await context.OpenAsync(req => req.Content(body), ct);
    }

    protected async Task<T?> GetJsonAsync<T>(string url, ScrapeOutcome outcome, CancellationToken ct)
    {
        var (_, body) = await FetchAsync(url, outcome, false, ct);
        if (body is null) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            if (outcome.Result == OutcomeResult.Ok) outcome.Result = OutcomeResult.Failed;
            outcome.FirstError ??= ex.Message;
            outcome.StopReason ??= $"failed: {ex.Message}";
            return default;
        }
    }

    private static int Requests(ScrapeOutcome o) => o.StatusCounts.Values.Sum();

    protected async Task<CanaryResult> ProbeHtmlAsync(
        string url, Func<IDocument, (int Tiles, int Valid)> inspect, CancellationToken ct)
    {
        var o = new ScrapeOutcome { Url = url };
        var doc = await GetHtmlAsync(url, o, ct);
        if (doc is null)
            return new CanaryResult(false, $"{url}: {o.Result} {o.FirstError}".Trim(), Requests(o));
        try
        {
            return ProbeVerdict(url, inspect(doc), Requests(o));
        }
        catch (Exception)
        {
            return new CanaryResult(false, $"{url}: unparseable response", Requests(o));
        }
    }

    protected async Task<CanaryResult> ProbeStringAsync(
        string url, Func<string, (int Tiles, int Valid)> inspect, CancellationToken ct)
    {
        var o = new ScrapeOutcome { Url = url };
        var body = await GetStringAsync(url, o, ct);
        if (body is null)
            return new CanaryResult(false, $"{url}: {o.Result} {o.FirstError}".Trim(), Requests(o));
        try
        {
            return ProbeVerdict(url, inspect(body), Requests(o));
        }
        catch (Exception)
        {
            return new CanaryResult(false, $"{url}: unparseable response", Requests(o));
        }
    }

    private static CanaryResult ProbeVerdict(string url, (int Tiles, int Valid) r, int requests)
        => r.Tiles == 0 ? new CanaryResult(false, $"{url}: no product tiles", requests)
         : r.Valid == 0 ? new CanaryResult(false, $"{url}: tiles missing name, price or id", requests)
         : new CanaryResult(true, "ok", requests);

    /// <summary>Re-runs failed targets once after a pause; successes replace the original as Recovered.</summary>
    protected async Task ReattemptAsync(
        List<ScrapeOutcome> outcomes, Func<ScrapeOutcome, Task<ScrapeOutcome>> rerun, CancellationToken ct)
    {
        if (Halted) return;
        var failed = outcomes.Where(o => o.Kind != OutcomeKind.Discovery &&
            o.Result != OutcomeResult.Ok && o.Result != OutcomeResult.Recovered &&
            o.Result != OutcomeResult.ContentPage).ToList();
        if (failed.Count == 0) return;
        await Task.Delay(ReattemptDelay, ct);
        foreach (var old in failed)
        {
            if (Halted) break;
            var fresh = await rerun(old);
            if (fresh.Result == OutcomeResult.Ok)
            {
                fresh.Result = OutcomeResult.Recovered;
                fresh.FirstError = old.FirstError;
                outcomes[outcomes.IndexOf(old)] = fresh;
            }
        }
    }

    /// <summary>Marks the run incomplete and warns that paging in a scope was stuck.</summary>
    protected void ReportLoop(PagingGuard guard, List<string> warnings)
    {
        Incomplete = true;
        warnings.Add($"Loop detected in {guard.Scope} at page {guard.PageNumber}: {guard.Reason}; category ended");
    }

    /// <summary>Builds the run result, adding the bound warning (if any) and the Incomplete flag.</summary>
    protected ScrapeRunResult BuildResult(
        IReadOnlyList<ScrapedProduct> products, IReadOnlyList<ScrapeOutcome> outcomes, IReadOnlyList<string> warnings)
    {
        var w = warnings.ToList();
        if (_boundWarning is not null) w.Add(_boundWarning);
        return new ScrapeRunResult(products, outcomes, w, Incomplete: Incomplete);
    }

    /// <summary>Run result for a scraper whose category discovery failed: no products, one failed Discovery outcome.</summary>
    protected static ScrapeRunResult DiscoveryFailed(string url, string reason) =>
        new([], [new ScrapeOutcome { Kind = OutcomeKind.Discovery, Key = "discovery", Url = url, Result = OutcomeResult.Failed, FirstError = reason }], []);

    protected static void MarkFirstPage(ScrapeOutcome o, int tilesSeen, int validTiles, bool contentPageWhenNoCounter = false)
    {
        if (o.Result != OutcomeResult.Ok) return;
        o.TilesSeen = tilesSeen;
        if (tilesSeen == 0)
        {
            if (contentPageWhenNoCounter && (o.SiteTotal is null or 0))
            {
                o.Result = OutcomeResult.ContentPage;
                o.StopReason = "content page: no tiles, no counter";
            }
            else o.Result = OutcomeResult.NoTiles;
        }
        else if (validTiles == 0) o.Result = OutcomeResult.MissingFields;
    }

    protected async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct = default)
    {
        var response = await GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
    }

    protected async Task<string> GetStringAsync(string url, CancellationToken ct = default)
    {
        var response = await GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    protected async Task<IDocument> GetHtmlAsync(string url, CancellationToken ct = default)
    {
        var response = await GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(ct);
        var context = BrowsingContext.New(Configuration.Default);
        return await context.OpenAsync(req => req.Content(html), ct);
    }
}
