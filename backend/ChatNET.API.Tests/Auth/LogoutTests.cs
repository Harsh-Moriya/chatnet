using System.Net;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Tests.Auth;

// Signing out has to do something on the server: the access token already out cannot be recalled, but
// the refresh token behind it must stop working, or "signed out" would only mean "forgotten by this tab".
[Collection(IntegrationTestCollection.Name)]
public class LogoutTests
{
    private readonly ChatApiFactory _factory;

    public LogoutTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Signing_out_ends_the_session_and_removes_the_cookie()
    {
        var user = await _factory.RegisterUserAsync("logout");
        using var client = _factory.CreateCookielessClient();
        var token = await client.SignInAsync(user);

        var response = await client.LogoutAsync(token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.DeletesRefreshCookie());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.RefreshAsync(token)).StatusCode);
    }

    // The family is what is revoked, not the one token, or a token the browser held a moment ago would
    // still be good for a refresh.
    [Fact]
    public async Task Signing_out_ends_every_token_of_the_session_not_only_the_current_one()
    {
        var user = await _factory.RegisterUserAsync("family");
        using var client = _factory.CreateCookielessClient();
        var first = await client.SignInAsync(user);
        var second = (await client.RefreshAsync(first)).GetRefreshCookie()!.Value.ToString();

        await client.LogoutAsync(second);

        Assert.All(await _factory.SessionRowsAsync(second), r => Assert.NotNull(r.RevokedAt));
    }

    [Fact]
    public async Task Signing_out_of_one_browser_leaves_the_others_signed_in()
    {
        var user = await _factory.RegisterUserAsync("browsers");
        using var client = _factory.CreateCookielessClient();
        var laptop = await client.SignInAsync(user);
        var phone = await client.SignInAsync(user);

        await client.LogoutAsync(laptop);

        Assert.Equal(HttpStatusCode.OK, (await client.RefreshAsync(phone)).StatusCode);
    }

    [Fact]
    public async Task Signing_out_without_a_cookie_or_with_a_dead_one_is_still_a_success()
    {
        using var client = _factory.CreateCookielessClient();

        var withoutCookie = await client.LogoutAsync(cookie: null);
        var unknown = await client.LogoutAsync("not-a-token-the-server-ever-issued");

        Assert.Equal(HttpStatusCode.NoContent, withoutCookie.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, unknown.StatusCode);
    }

    [Fact]
    public async Task Signing_out_twice_is_fine()
    {
        var user = await _factory.RegisterUserAsync("twice");
        using var client = _factory.CreateCookielessClient();
        var token = await client.SignInAsync(user);

        await client.LogoutAsync(token);
        var again = await client.LogoutAsync(token);

        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
    }

    [Fact]
    public async Task A_sign_out_request_without_the_client_header_is_refused_and_ends_nothing()
    {
        var user = await _factory.RegisterUserAsync("forged");
        using var client = _factory.CreateCookielessClient();
        var token = await client.SignInAsync(user);

        var response = await client.LogoutAsync(token, withHeader: false);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.RefreshAsync(token)).StatusCode);
    }
}
