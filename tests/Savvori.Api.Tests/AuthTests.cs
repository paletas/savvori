using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Savvori.Api.Tests.Infrastructure;

namespace Savvori.Api.Tests;

public class AuthTests : IClassFixture<SavvoriWebApiFactory>
{
    private readonly SavvoriWebApiFactory _factory;
    private readonly HttpClient _client;

    public AuthTests(SavvoriWebApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Register_NewEmail_Returns200()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/register",
            new { Email = $"new_{Guid.NewGuid()}@test.com", Password = "Password123!" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Register_DuplicateEmail_Returns400()
    {
        var email = $"dup_{Guid.NewGuid()}@test.com";
        await _client.PostAsJsonAsync("/api/auth/register",
            new { Email = email, Password = "Password123!" });

        var response = await _client.PostAsJsonAsync("/api/auth/register",
            new { Email = email, Password = "Password123!" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_ValidCredentials_Returns200WithToken()
    {
        var email = $"login_{Guid.NewGuid()}@test.com";
        var password = "Password123!";
        await _client.PostAsJsonAsync("/api/auth/register",
            new { Email = email, Password = password });

        var response = await _client.PostAsJsonAsync("/api/auth/login",
            new { Email = email, Password = password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("token", out var tokenProp));
        Assert.False(string.IsNullOrWhiteSpace(tokenProp.GetString()));
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401()
    {
        var email = $"wrong_{Guid.NewGuid()}@test.com";
        await _client.PostAsJsonAsync("/api/auth/register",
            new { Email = email, Password = "Password123!" });

        var response = await _client.PostAsJsonAsync("/api/auth/login",
            new { Email = email, Password = "WrongPassword!" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_UnknownEmail_Returns401()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login",
            new { Email = "nobody@nowhere.com", Password = "SomePass123!" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
