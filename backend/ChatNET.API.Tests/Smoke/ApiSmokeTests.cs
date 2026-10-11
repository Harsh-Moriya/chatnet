using System.Net;
using System.Net.Http.Json;
using ChatNET.API.Auth.Models;
using ChatNET.API.Tests.Infrastructure;

namespace ChatNET.API.Tests.Smoke;

// These tests prove the test infrastructure itself works end to end: the containers start,
// migrations apply, the API boots with the real pipeline, and authentication round-trips.
// Feature behaviour is tested in the feature folders.
[Collection(IntegrationTestCollection.Name)]
public class ApiSmokeTests
{
    private readonly ChatApiFactory _factory;

    public ApiSmokeTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Health_endpoint_reports_healthy()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Protected_endpoint_rejects_anonymous_requests()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/conversations");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Registered_user_can_log_in_and_use_the_token()
    {
        var user = await _factory.RegisterUserAsync();
        using var anonymous = _factory.CreateClient();

        var login = await anonymous.PostAsJsonAsync("/api/auth/login",
            new { email = user.Email, password = user.Password });
        login.EnsureSuccessStatusCode();
        var auth = await login.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(auth);
        Assert.Equal(user.Id.ToString(), auth.UserId);

        // The sidebar query touches the migrated tables, so a 200 with an empty list also
        // shows the schema was created.
        using var authed = _factory.CreateClientFor(user);
        var conversations = await authed.GetAsync("/api/conversations");

        Assert.Equal(HttpStatusCode.OK, conversations.StatusCode);
        Assert.Equal("[]", await conversations.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_with_wrong_password_returns_unauthorized()
    {
        var user = await _factory.RegisterUserAsync();
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { email = user.Email, password = user.Password + "x" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
