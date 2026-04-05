using System.Net;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Savvori.WebApp.Services;

namespace Savvori.Web.Tests.WebApp;

public class AuthCookieHandlerTests
{
    [Fact]
    public async Task SendAsync_WithTokenCookie_AddsAuthorizationHeader()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        var cookies = Substitute.For<IRequestCookieCollection>();
        cookies["savvori_token"].Returns("test-jwt-token");
        var request = Substitute.For<HttpRequest>();
        request.Cookies.Returns(cookies);
        var ctx = Substitute.For<HttpContext>();
        ctx.Request.Returns(request);
        accessor.HttpContext.Returns(ctx);

        HttpRequestMessage? captured = null;
        var inner = new DelegateHandler(req =>
        {
            captured = req;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var handler = new AuthCookieHandler(accessor) { InnerHandler = inner };
        var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://api.test/test"), CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("Bearer", captured.Headers.Authorization?.Scheme);
        Assert.Equal("test-jwt-token", captured.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task SendAsync_WithoutTokenCookie_DoesNotAddAuthorizationHeader()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        var cookies = Substitute.For<IRequestCookieCollection>();
        cookies["savvori_token"].Returns((string?)null);
        var request = Substitute.For<HttpRequest>();
        request.Cookies.Returns(cookies);
        var ctx = Substitute.For<HttpContext>();
        ctx.Request.Returns(request);
        accessor.HttpContext.Returns(ctx);

        HttpRequestMessage? captured = null;
        var inner = new DelegateHandler(req =>
        {
            captured = req;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var handler = new AuthCookieHandler(accessor) { InnerHandler = inner };
        var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://api.test/test"), CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Null(captured.Headers.Authorization);
    }

    [Fact]
    public async Task SendAsync_WithEmptyTokenCookie_DoesNotAddAuthorizationHeader()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        var cookies = Substitute.For<IRequestCookieCollection>();
        cookies["savvori_token"].Returns(string.Empty);
        var request = Substitute.For<HttpRequest>();
        request.Cookies.Returns(cookies);
        var ctx = Substitute.For<HttpContext>();
        ctx.Request.Returns(request);
        accessor.HttpContext.Returns(ctx);

        HttpRequestMessage? captured = null;
        var inner = new DelegateHandler(req =>
        {
            captured = req;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var handler = new AuthCookieHandler(accessor) { InnerHandler = inner };
        var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://api.test/test"), CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Null(captured.Headers.Authorization);
    }

    [Fact]
    public async Task SendAsync_WithNullHttpContext_DoesNotAddAuthorizationHeader()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns((HttpContext?)null);

        HttpRequestMessage? captured = null;
        var inner = new DelegateHandler(req =>
        {
            captured = req;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var handler = new AuthCookieHandler(accessor) { InnerHandler = inner };
        var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://api.test/test"), CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Null(captured.Headers.Authorization);
    }
}

/// <summary>Helper for testing DelegatingHandler without a real HTTP server.</summary>
file class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
}
