using System.Net;
using Savvori.WebApi.Scraping;
using Savvori.Shared;

namespace Savvori.Web.Tests;

/// <summary>
/// Fake HttpMessageHandler that returns pre-configured HTML responses.
/// </summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, (HttpStatusCode Status, string Content)> _routes = new();
    private string _defaultContent = "<html><body></body></html>";

    public void SetupRoute(string urlContains, string html, HttpStatusCode status = HttpStatusCode.OK)
        => _routes[urlContains] = (status, html);

    private readonly Dictionary<string, (HttpStatusCode Status, string Content)[]> _sequences = new();
    private readonly Dictionary<string, int> _sequenceIndex = new();

    /// <summary>Serves the responses in order for matching URLs; the last one repeats.</summary>
    public void SetupRouteSequence(string urlContains, params (HttpStatusCode, string)[] responses)
    {
        _sequences[urlContains] = responses;
        _sequenceIndex[urlContains] = 0;
    }

    private readonly Dictionary<string, string> _redirects = new();

    /// <summary>Makes responses for matching URLs look like they were served from <paramref name="finalUrl"/> (as after a followed redirect).</summary>
    public void SetupRedirect(string urlContains, string finalUrl)
        => _redirects[urlContains] = finalUrl;

    public void SetDefaultResponse(string html)
        => _defaultContent = html;

    private int _requestCount;

    /// <summary>Total number of HTTP requests processed by this handler.</summary>
    public int RequestCount => _requestCount;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requestCount);
        var url = request.RequestUri?.ToString() ?? string.Empty;
        (HttpStatusCode Status, string Content)? seq = null;
        lock (_sequences)
        {
            foreach (var kv in _sequences)
            {
                if (!url.Contains(kv.Key, StringComparison.OrdinalIgnoreCase)) continue;
                var i = _sequenceIndex[kv.Key];
                seq = kv.Value[Math.Min(i, kv.Value.Length - 1)];
                _sequenceIndex[kv.Key] = i + 1;
                break;
            }
        }
        var (status, content) = seq ?? _routes
            .Where(kv => url.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
            .Select(kv => (kv.Value.Status, kv.Value.Content))
            .FirstOrDefault((HttpStatusCode.OK, _defaultContent));

        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(content, System.Text.Encoding.UTF8, "text/html")
        };
        var redirect = _redirects.Where(kv => url.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Value).FirstOrDefault();
        if (redirect != null)
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, redirect);
        return Task.FromResult(response);
    }
}
