using System.Net;
using System.Text.Json;
using AngleSharp;
using AngleSharp.Dom;
using Polly;
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
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .HandleResult(r =>
                        r.StatusCode == HttpStatusCode.TooManyRequests ||
                        r.StatusCode == HttpStatusCode.ServiceUnavailable ||
                        (int)r.StatusCode >= 500),
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

    public abstract Task<IReadOnlyList<ScrapedProduct>> ScrapeProductsAsync(
        string? category = null, CancellationToken ct = default);

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
