using System.Net;
using System.Net.Http.Json;
using ChatNET.API.Auth;
using ChatNET.API.Tests.Infrastructure;

namespace ChatNET.API.Tests.Auth;

[Collection(IntegrationTestCollection.Name)]
public class NativeAuthTests
{
    private readonly ChatApiFactory _factory;

    public NativeAuthTests(ChatApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Registration_returns_a_native_session_without_setting_a_browser_cookie()
    {
        using var client = _factory.CreateCookielessClient();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var response = await client.PostAsJsonAsync("/api/auth/native/register", new
        {
            userName = $"native_{suffix}", displayName = "Native", email = $"native_{suffix}@example.test",
            password = "Aa1-registration-pass",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertPrivate(response);
        var body = await response.Content.ReadFromJsonAsync<NativeAuthResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body.Token));
        Assert.False(string.IsNullOrWhiteSpace(body.RefreshToken));
        Assert.True(body.RefreshTokenExpiresAt > DateTime.UtcNow);
    }

    [Fact]
    public async Task Native_rotation_rejects_stale_token_then_logout_revokes_the_current_session()
    {
        var user = await _factory.RegisterUserAsync("native");
        using var client = _factory.CreateCookielessClient();
        client.DefaultRequestHeaders.Add("Cookie", "chatnet_refresh=browser-cookie");
        var login = await client.PostAsJsonAsync("/api/auth/native/login", new { email = user.Email, password = user.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        AssertPrivate(login);
        var first = (await login.Content.ReadFromJsonAsync<NativeAuthResponse>())!;

        var renewal = await client.PostAsJsonAsync("/api/auth/native/refresh", new { refreshToken = first.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, renewal.StatusCode);
        AssertPrivate(renewal);
        var second = (await renewal.Content.ReadFromJsonAsync<NativeAuthResponse>())!;
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        Assert.Equal(user.Id.ToString(), second.UserId);

        var stale = await client.PostAsJsonAsync("/api/auth/native/refresh", new { refreshToken = first.RefreshToken });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        AssertPrivate(stale);
        var logout = await client.PostAsJsonAsync("/api/auth/native/logout", new { refreshToken = second.RefreshToken });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        AssertPrivate(logout);
        var rejected = await client.PostAsJsonAsync("/api/auth/native/refresh", new { refreshToken = second.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        AssertPrivate(rejected);
    }

    [Fact]
    public async Task Native_refresh_uses_the_body_and_does_not_clear_a_browser_cookie_on_failure()
    {
        var user = await _factory.RegisterUserAsync("nativecookie");
        using var client = _factory.CreateCookielessClient();
        var browserToken = await client.SignInAsync(user);
        client.DefaultRequestHeaders.Add("Cookie", $"chatnet_refresh={browserToken}");

        var rejected = await client.PostAsJsonAsync("/api/auth/native/refresh", new { refreshToken = "unknown" });
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        AssertPrivate(rejected);
        Assert.Equal(HttpStatusCode.OK, (await client.RefreshAsync(browserToken)).StatusCode);
    }

    [Fact]
    public async Task Renewal_reports_the_effective_device_even_when_a_different_device_is_requested()
    {
        var (user, bearer) = await _factory.UserWithKeysAsync();
        using (bearer)
        using (var client = _factory.CreateCookielessClient())
        {
            var device = await bearer.AddDeviceAsync();
            var other = await bearer.AddDeviceAsync();
            client.DefaultRequestHeaders.Add("X-Device-Id", device.ToString());
            var login = await client.PostAsJsonAsync("/api/auth/native/login", new { email = user.Email, password = user.Password });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            var first = (await login.Content.ReadFromJsonAsync<NativeAuthResponse>())!;
            Assert.Equal(device, first.DeviceId);
            client.DefaultRequestHeaders.Remove("X-Device-Id");
            client.DefaultRequestHeaders.Add("X-Device-Id", other.ToString());
            var renewal = await client.PostAsJsonAsync("/api/auth/native/refresh", new { refreshToken = first.RefreshToken });
            Assert.Equal(HttpStatusCode.OK, renewal.StatusCode);
            var second = (await renewal.Content.ReadFromJsonAsync<NativeAuthResponse>())!;
            Assert.Equal(device, second.DeviceId);
        }
    }

    private static void AssertPrivate(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }
}
