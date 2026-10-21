using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ChatNET.API.Auth;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace ChatNET.API.Tests.Auth;

// The access token lives for minutes and cannot be taken back; the refresh token behind it lives for
// weeks, can be revoked, and works once. These tests cover what the refresh token is and what a refresh
// does. What happens when a token is presented twice is in RefreshTokenReuseTests.
[Collection(IntegrationTestCollection.Name)]
public class RefreshTokenTests
{
    private readonly ChatApiFactory _factory;

    public RefreshTokenTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Signing_in_sets_a_refresh_cookie_that_scripts_cannot_read_and_other_sites_cannot_trigger()
    {
        var user = await _factory.RegisterUserAsync("cookie");
        using var client = _factory.CreateCookielessClient();

        var response = await client.LoginAsync(user);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = response.GetRefreshCookie();
        Assert.NotNull(cookie);
        Assert.True(cookie.HttpOnly, "Scripts on the page must not be able to read the token.");
        Assert.True(cookie.Secure, "The token must only travel over https.");
        Assert.Equal(SameSiteMode.Strict, cookie.SameSite);
        Assert.Equal(RefreshCookie.CookiePath, cookie.Path.ToString());
        Assert.NotEmpty(cookie.Value.ToString());
        Assert.InRange(cookie.Expires!.Value, DateTimeOffset.UtcNow.AddDays(14).AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(14).AddMinutes(1));
    }

    [Fact]
    public async Task Registering_sets_the_cookie_too()
    {
        using var client = _factory.CreateCookielessClient();
        var suffix = Guid.NewGuid().ToString("N")[..10];

        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            userName = $"reg_{suffix}",
            displayName = "Reg",
            email = $"reg_{suffix}@example.test",
            password = "Aa1-registration-pass",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.GetRefreshCookie());
    }

    [Fact]
    public async Task The_refresh_token_is_never_in_the_body()
    {
        var user = await _factory.RegisterUserAsync("body");
        using var client = _factory.CreateCookielessClient();

        var response = await client.LoginAsync(user);

        var cookie = response.GetRefreshCookie()!.Value.ToString();
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(cookie, body);
        Assert.DoesNotContain("refresh", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_access_token_is_short_lived_and_the_response_says_when_it_ends_in_whole_seconds()
    {
        var user = await _factory.RegisterUserAsync("short");
        using var client = _factory.CreateCookielessClient();

        var body = await (await client.LoginAsync(user)).ReadJsonAsync();

        var token = new JwtSecurityTokenHandler().ReadJwtToken(body.GetProperty("token").GetString());
        var announced = body.GetProperty("accessTokenExpiresAt");
        Assert.InRange(token.ValidTo, DateTime.UtcNow.AddMinutes(14), DateTime.UtcNow.AddMinutes(16));
        Assert.Equal(token.ValidTo, announced.GetDateTime().ToUniversalTime());

        // The browser parses this text. A fraction of seconds with more than three digits is outside what the
        // JavaScript date format promises to read, so there must be none.
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", announced.GetString());
    }

    // A copy of the table must not be a copy of everyone's sessions.
    [Fact]
    public async Task Only_a_hash_of_the_token_is_stored()
    {
        var user = await _factory.RegisterUserAsync("hash");
        using var client = _factory.CreateCookielessClient();

        var token = await client.SignInAsync(user);

        var row = Assert.Single(await _factory.SessionRowsAsync(token));
        Assert.Equal(AuthApi.HashOf(token), row.TokenHash);
        Assert.NotEqual(token, row.TokenHash);
    }

    [Fact]
    public async Task Refreshing_returns_a_working_access_token_and_a_new_cookie()
    {
        var user = await _factory.RegisterUserAsync("rotate");
        using var client = _factory.CreateCookielessClient();
        var first = await client.SignInAsync(user);

        var response = await client.RefreshAsync(first);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var second = response.GetRefreshCookie()!.Value.ToString();
        Assert.NotEqual(first, second);

        var body = await response.ReadJsonAsync();
        Assert.Equal(user.Id.ToString(), body.GetProperty("userId").GetString());
        Assert.Equal(user.UserName, body.GetProperty("userName").GetString());

        using var authorized = _factory.CreateCookielessClient();
        authorized.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
        Assert.Equal(HttpStatusCode.OK, (await authorized.GetAsync("/api/conversations")).StatusCode);
    }

    [Fact]
    public async Task A_refresh_retires_the_old_token_and_keeps_the_chain_in_one_family()
    {
        var user = await _factory.RegisterUserAsync("chain");
        using var client = _factory.CreateCookielessClient();
        var first = await client.SignInAsync(user);
        var second = (await client.RefreshAsync(first)).GetRefreshCookie()!.Value.ToString();
        var third = (await client.RefreshAsync(second)).GetRefreshCookie()!.Value.ToString();

        var rows = await _factory.SessionRowsAsync(first);

        Assert.Equal(3, rows.Count);
        Assert.Single(rows.Select(r => r.FamilyId).Distinct());
        var active = Assert.Single(rows, r => r.RevokedAt is null);
        Assert.Equal(AuthApi.HashOf(third), active.TokenHash);
        var retired = rows.Where(r => r.RevokedAt is not null).ToList();
        Assert.Equal(2, retired.Count);
        Assert.All(retired, r => Assert.NotNull(r.ReplacedById));
        var firstRow = Assert.Single(rows, r => r.TokenHash == AuthApi.HashOf(first));
        Assert.Equal(rows.Single(r => r.TokenHash == AuthApi.HashOf(second)).Id, firstRow.ReplacedById);
    }

    [Fact]
    public async Task Each_sign_in_is_its_own_family()
    {
        var user = await _factory.RegisterUserAsync("families");
        using var client = _factory.CreateCookielessClient();

        var first = await client.SignInAsync(user);
        var second = await client.SignInAsync(user);

        var families = new[] { first, second }.Select(async t => (await _factory.SessionRowsAsync(t)).Single().FamilyId);
        Assert.Equal(2, (await Task.WhenAll(families)).Distinct().Count());
    }

    [Fact]
    public async Task A_request_without_the_cookie_is_refused_and_has_nothing_to_remove()
    {
        using var client = _factory.CreateCookielessClient();

        var response = await client.RefreshAsync(cookie: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.GetRefreshCookie());
        Assert.Equal(SessionRejectedMessage, (await response.ReadProblemAsync()).Detail);
    }

    [Fact]
    public async Task An_unknown_token_is_refused_and_its_cookie_is_removed()
    {
        using var client = _factory.CreateCookielessClient();

        var response = await client.RefreshAsync("not-a-token-the-server-ever-issued");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.DeletesRefreshCookie(), "A cookie that can never work should be removed.");
    }

    // Without the header a page on another site could trigger the call; with the cookie held back as well
    // (SameSite) this is the second layer.
    [Fact]
    public async Task A_request_without_the_client_header_is_refused_and_does_not_use_up_the_token()
    {
        var user = await _factory.RegisterUserAsync("header");
        using var client = _factory.CreateCookielessClient();
        var token = await client.SignInAsync(user);

        var refused = await client.RefreshAsync(token, withHeader: false);
        var accepted = await client.RefreshAsync(token);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Null(refused.GetRefreshCookie());
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task An_expired_refresh_token_is_refused_and_its_cookie_is_removed()
    {
        var user = await _factory.RegisterUserAsync("expiring");
        var clock = new ManualTimeProvider();
        using var host = _factory.WithClock(clock);
        using var client = host.CreateCookielessClient();
        var token = await client.SignInAsync(user);

        clock.Advance(TimeSpan.FromDays(14).Add(TimeSpan.FromMinutes(1)));
        var response = await client.RefreshAsync(token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.DeletesRefreshCookie());
    }

    [Fact]
    public async Task A_token_with_time_left_still_works_after_a_long_time()
    {
        var user = await _factory.RegisterUserAsync("lasting");
        var clock = new ManualTimeProvider();
        using var host = _factory.WithClock(clock);
        using var client = host.CreateCookielessClient();
        var token = await client.SignInAsync(user);

        clock.Advance(TimeSpan.FromDays(13));
        var response = await client.RefreshAsync(token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Using the app at least once every two weeks keeps the session: each refresh issues a token that is
    // good for another two weeks from that moment.
    [Fact]
    public async Task Every_refresh_extends_the_session()
    {
        var user = await _factory.RegisterUserAsync("sliding");
        var clock = new ManualTimeProvider();
        using var host = _factory.WithClock(clock);
        using var client = host.CreateCookielessClient();
        var token = await client.SignInAsync(user);

        for (var week = 0; week < 4; week++)
        {
            clock.Advance(TimeSpan.FromDays(10));
            var response = await client.RefreshAsync(token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            token = response.GetRefreshCookie()!.Value.ToString();
        }
    }

    private const string SessionRejectedMessage = "Your session has expired. Sign in again.";
}
