using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Savvori.Api.Tests.Infrastructure;

namespace Savvori.Api.Tests;

public class ScraperHttpClientTests : IClassFixture<SavvoriWebApiFactory>
{
    private readonly SavvoriWebApiFactory _factory;

    public ScraperHttpClientTests(SavvoriWebApiFactory factory) => _factory = factory;

    [Theory]
    [InlineData("continente")]
    [InlineData("pingodoce")]
    [InlineData("auchan")]
    [InlineData("lidl")]
    public void ScraperClients_PrimaryHandler_DecompressesGzipDeflateBrotli(string name)
    {
        var sp = _factory.Services;
        var options = sp.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>().Get(name);
        var builder = sp.GetRequiredService<HttpMessageHandlerBuilder>();
        builder.Name = name;
        foreach (var action in options.HttpMessageHandlerBuilderActions) action(builder);

        var handler = Assert.IsType<SocketsHttpHandler>(builder.PrimaryHandler);
        Assert.Equal(
            DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            handler.AutomaticDecompression);
    }
}
