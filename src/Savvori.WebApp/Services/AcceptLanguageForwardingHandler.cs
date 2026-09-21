namespace Savvori.WebApp.Services;

/// <summary>
/// Passes the browser's Accept-Language on to the API, so category names come back in the visitor's language
/// (the API falls back to Portuguese for languages it has no translation for).
/// </summary>
public sealed class AcceptLanguageForwardingHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var incoming = accessor.HttpContext?.Request.Headers.AcceptLanguage.ToString();
        if (!string.IsNullOrWhiteSpace(incoming) && !request.Headers.Contains("Accept-Language"))
            request.Headers.TryAddWithoutValidation("Accept-Language", incoming);
        return base.SendAsync(request, cancellationToken);
    }
}
