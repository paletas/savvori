using System.Net;
using System.Text.Json;
using AngleSharp;
using AngleSharp.Dom;
using Polly;
using Savvori.Shared;
using Polly.Retry;

namespace Savvori.WebApi.Scraping;

/// <summary>
/// Abstract base for all store scrapers. Provides rate-limited, retrying HTTP helpers.
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
                    if (args.Outcome.Exception is HttpRequestException) return true;
                    var r = args.Outcome.Result;
                    if (r is null) return false;
                    if (r.StatusCode == HttpStatusCode.TooManyRequests ||
                        r.StatusCode == HttpStatusCode.ServiceUnavailable ||
                        r.StatusCode == HttpStatusCode.Forbidden ||
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
        }

        HttpResponseMessage resp;
        try
        {
            resp = await GetAsync(url, outcome, ct);
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

        if (!resp.IsSuccessStatusCode)
        {
            Fail(resp.StatusCode == HttpStatusCode.Forbidden ? OutcomeResult.Blocked : OutcomeResult.Failed,
                $"HTTP {(int)resp.StatusCode}");
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
        var failed = outcomes.Where(o => o.Kind != OutcomeKind.Discovery &&
            o.Result != OutcomeResult.Ok && o.Result != OutcomeResult.Recovered).ToList();
        if (failed.Count == 0) return;
        await Task.Delay(ReattemptDelay, ct);
        foreach (var old in failed)
        {
            var fresh = await rerun(old);
            if (fresh.Result == OutcomeResult.Ok)
            {
                fresh.Result = OutcomeResult.Recovered;
                fresh.FirstError = old.FirstError;
                outcomes[outcomes.IndexOf(old)] = fresh;
            }
        }
    }

    /// <summary>Run result for a scraper whose category discovery failed: no products, one failed Discovery outcome.</summary>
    protected static ScrapeRunResult DiscoveryFailed(string url, string reason) =>
        new([], [new ScrapeOutcome { Kind = OutcomeKind.Discovery, Key = "discovery", Url = url, Result = OutcomeResult.Failed, FirstError = reason }], []);

    protected static void MarkFirstPage(ScrapeOutcome o, int tilesSeen, int validTiles)
    {
        if (o.Result != OutcomeResult.Ok) return;
        o.TilesSeen = tilesSeen;
        if (tilesSeen == 0) o.Result = OutcomeResult.NoTiles;
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
