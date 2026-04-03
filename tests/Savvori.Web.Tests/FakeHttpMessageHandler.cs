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
        var (status, content) = _routes
            .Where(kv => url.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
            .Select(kv => (kv.Value.Status, kv.Value.Content))
            .FirstOrDefault((HttpStatusCode.OK, _defaultContent));

        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(content, System.Text.Encoding.UTF8, "text/html")
        };
        return Task.FromResult(response);
    }
}
