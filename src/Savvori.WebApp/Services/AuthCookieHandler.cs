namespace Savvori.WebApp.Services;

/// <summary>
/// Reads the JWT token from the "savvori_token" cookie and adds it as a Bearer Authorization header
/// to all outgoing requests to the WebApi.
/// </summary>
public class AuthCookieHandler(IHttpContextAccessor httpContextAccessor) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = httpContextAccessor.HttpContext?.Request.Cookies["savvori_token"];
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        return base.SendAsync(request, cancellationToken);
    }
}
