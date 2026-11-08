using System.Net;
using System.Net.Http.Json;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Tests.Devices;

// A session is tied to a device by the X-Device-Id header on sign-in and on renewal, so removing the device ends the sessions
// that were signed in on it and no others.
[Collection(IntegrationTestCollection.Name)]
public class DeviceSessionTests
{
    private const string DeviceHeader = "X-Device-Id";

    private readonly ChatApiFactory _factory;

    public DeviceSessionTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, TestUser user, string? device)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email = user.Email, password = user.Password }),
        };
        if (device is not null)
            request.Headers.Add(DeviceHeader, device);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string cookie, string? device)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        request.Headers.Add("Cookie", $"chatnet_refresh={cookie}");
        request.Headers.Add("X-Requested-With", "ChatNET");
        if (device is not null)
            request.Headers.Add(DeviceHeader, device);
        return client.SendAsync(request);
    }

    private static string CookieOf(HttpResponseMessage response) =>
        response.GetRefreshCookie()?.Value.ToString() ?? throw new InvalidOperationException("No refresh cookie.");

    private async Task<(TestUser User, HttpClient Bearer, Guid Device, HttpClient Cookieless)> ArrangeAsync()
    {
        var (user, bearer) = await _factory.UserWithKeysAsync();
        var device = await bearer.AddDeviceAsync();
        return (user, bearer, device, _factory.CreateCookielessClient());
    }

    // ##### Tying a session to a device #####

    [Fact]
    public async Task Signing_in_with_the_device_header_ties_the_session_and_every_renewal_keeps_it()
    {
        var (user, _, device, client) = await ArrangeAsync();

        var login = await LoginAsync(client, user, device.ToString());
        var renewed = await RefreshAsync(client, CookieOf(login), device: null);

        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        var rows = await _factory.SessionRowsAsync(CookieOf(renewed));
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(device, row.DeviceId));
    }

    [Fact]
    public async Task A_renewal_with_the_header_ties_a_session_that_was_not_tied_when_it_started()
    {
        var (user, _, device, client) = await ArrangeAsync();
        var login = await LoginAsync(client, user, device: null);

        var renewed = await RefreshAsync(client, CookieOf(login), device.ToString());

        var rows = await _factory.SessionRowsAsync(CookieOf(renewed));
        Assert.Null(rows[0].DeviceId);
        Assert.Equal(device, rows[1].DeviceId);
    }

    [Fact]
    public async Task A_session_that_is_tied_stays_tied_to_its_device_even_if_a_later_renewal_names_another()
    {
        var (user, bearer, device, client) = await ArrangeAsync();
        var other = await bearer.AddDeviceAsync();
        var login = await LoginAsync(client, user, device.ToString());

        var renewed = await RefreshAsync(client, CookieOf(login), other.ToString());

        var rows = await _factory.SessionRowsAsync(CookieOf(renewed));
        Assert.All(rows, row => Assert.Equal(device, row.DeviceId));
    }

    [Fact]
    public async Task Without_the_header_a_session_is_not_tied_to_anything()
    {
        var (user, _, _, client) = await ArrangeAsync();

        var login = await LoginAsync(client, user, device: null);

        var rows = await _factory.SessionRowsAsync(CookieOf(login));
        Assert.All(rows, row => Assert.Null(row.DeviceId));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task A_header_that_names_no_device_is_ignored(string header)
    {
        var (user, _, _, client) = await ArrangeAsync();

        var login = await LoginAsync(client, user, header);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.All(await _factory.SessionRowsAsync(CookieOf(login)), row => Assert.Null(row.DeviceId));
    }

    // A person cannot tie their session to the device of someone else, nor can removing that device reach their session.
    [Fact]
    public async Task A_header_that_names_another_persons_device_is_ignored_and_that_persons_removal_does_not_end_the_session()
    {
        var (_, aliceBearer, alicesDevice, _) = await ArrangeAsync();
        var (bob, _, _, client) = await ArrangeAsync();

        var login = await LoginAsync(client, bob, alicesDevice.ToString());
        await aliceBearer.DeleteAsync($"/api/devices/{alicesDevice}");

        Assert.All(await _factory.SessionRowsAsync(CookieOf(login)), row => Assert.Null(row.DeviceId));
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(client, CookieOf(login), device: null)).StatusCode);
    }

    [Fact]
    public async Task A_header_that_names_a_removed_device_is_ignored()
    {
        var (user, bearer, device, client) = await ArrangeAsync();
        await bearer.DeleteAsync($"/api/devices/{device}");

        var login = await LoginAsync(client, user, device.ToString());

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.All(await _factory.SessionRowsAsync(CookieOf(login)), row => Assert.Null(row.DeviceId));
    }

    // ##### Removing a device #####

    [Fact]
    public async Task Removing_a_device_ends_the_session_that_was_signed_in_on_it()
    {
        var (user, bearer, device, client) = await ArrangeAsync();
        var login = await LoginAsync(client, user, device.ToString());
        var renewed = await RefreshAsync(client, CookieOf(login), device.ToString());
        var cookie = CookieOf(renewed);

        await bearer.DeleteAsync($"/api/devices/{device}");

        // Revoked by the removal itself, before any renewal has been tried.
        Assert.All(await _factory.SessionRowsAsync(cookie), row => Assert.NotNull(row.RevokedAt));
        var afterwards = await RefreshAsync(client, cookie, device.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, afterwards.StatusCode);
    }

    // A session that was tied by a later renewal is ended too: its newest token carries the device.
    [Fact]
    public async Task Removing_a_device_ends_a_session_that_was_tied_by_a_later_renewal()
    {
        var (user, bearer, device, client) = await ArrangeAsync();
        var login = await LoginAsync(client, user, device: null);
        var renewed = await RefreshAsync(client, CookieOf(login), device.ToString());

        await bearer.DeleteAsync($"/api/devices/{device}");

        var rows = await _factory.SessionRowsAsync(CookieOf(renewed));
        Assert.All(rows, row => Assert.NotNull(row.RevokedAt));
    }

    // A renewal that overlaps a removal can leave a token the removal did not see. The device is removed and that token still works,
    // so the renewal itself must notice and end the session.
    [Fact]
    public async Task A_session_tied_to_a_removed_device_cannot_be_renewed_even_if_its_token_was_missed()
    {
        var (user, _, device, client) = await ArrangeAsync();
        var cookie = CookieOf(await LoginAsync(client, user, device.ToString()));
        await _factory.ExecuteDbAsync(db => db.Devices.Where(d => d.Id == device).ExecuteUpdateAsync(s => s.SetProperty(d => d.RemovedAt, DateTime.UtcNow)));

        var renewed = await RefreshAsync(client, cookie, device.ToString());

        Assert.Equal(HttpStatusCode.Unauthorized, renewed.StatusCode);
        Assert.True(renewed.DeletesRefreshCookie());
        Assert.All(await _factory.SessionRowsAsync(cookie), row => Assert.NotNull(row.RevokedAt));
    }

    [Fact]
    public async Task Removing_a_device_leaves_the_sessions_of_other_devices_and_untied_ones_alone()
    {
        var (user, bearer, device, client) = await ArrangeAsync();
        var other = await bearer.AddDeviceAsync();
        var onOther = CookieOf(await LoginAsync(client, user, other.ToString()));
        var untied = CookieOf(await LoginAsync(client, user, device: null));
        await LoginAsync(client, user, device.ToString());

        await bearer.DeleteAsync($"/api/devices/{device}");

        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(client, onOther, other.ToString())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(client, untied, device: null)).StatusCode);
    }

    [Fact]
    public async Task Removing_a_device_that_does_not_exist_ends_no_session()
    {
        var (user, bearer, device, client) = await ArrangeAsync();
        var cookie = CookieOf(await LoginAsync(client, user, device.ToString()));

        await bearer.DeleteAsync($"/api/devices/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(client, cookie, device.ToString())).StatusCode);
    }

    // ##### Last active #####

    [Fact]
    public async Task A_device_is_marked_active_when_its_session_signs_in_or_is_renewed()
    {
        var clock = new ManualTimeProvider();
        using var host = _factory.WithClock(clock);
        var user = await _factory.RegisterUserWithoutKeysAsync("active");
        using var bearer = host.ClientFor(user);
        await bearer.SetUpKeysAsync();
        var device = await bearer.AddDeviceAsync();
        using var client = host.CreateCookielessClient();
        var created = (await _factory.QueryDbAsync(db => db.Devices.SingleAsync(d => d.Id == device))).LastActiveAt;

        clock.Advance(TimeSpan.FromHours(2));
        var login = await LoginAsync(client, user, device.ToString());
        var afterLogin = (await _factory.QueryDbAsync(db => db.Devices.SingleAsync(d => d.Id == device))).LastActiveAt;

        clock.Advance(TimeSpan.FromHours(3));
        await RefreshAsync(client, CookieOf(login), device: null);
        var afterRefresh = (await _factory.QueryDbAsync(db => db.Devices.SingleAsync(d => d.Id == device))).LastActiveAt;

        Assert.Equal(created.AddHours(2), afterLogin, TimeSpan.FromMilliseconds(5));
        Assert.Equal(created.AddHours(5), afterRefresh, TimeSpan.FromMilliseconds(5));
    }

    [Fact]
    public async Task A_renewal_that_names_no_device_does_not_touch_any()
    {
        var clock = new ManualTimeProvider();
        using var host = _factory.WithClock(clock);
        var user = await _factory.RegisterUserWithoutKeysAsync("quiet");
        using var bearer = host.ClientFor(user);
        await bearer.SetUpKeysAsync();
        var device = await bearer.AddDeviceAsync();
        using var client = host.CreateCookielessClient();
        var before = (await _factory.QueryDbAsync(db => db.Devices.SingleAsync(d => d.Id == device))).LastActiveAt;

        clock.Advance(TimeSpan.FromHours(4));
        var login = await LoginAsync(client, user, device: null);
        await RefreshAsync(client, CookieOf(login), device: null);

        Assert.Equal(before, (await _factory.QueryDbAsync(db => db.Devices.SingleAsync(d => d.Id == device))).LastActiveAt);
    }

    // The header is not part of the sign-in body: a body that carries a device id does not tie the session.
    [Fact]
    public async Task A_device_id_in_the_sign_in_body_does_not_tie_the_session()
    {
        var (user, _, device, client) = await ArrangeAsync();

        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = user.Password, deviceId = device });

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.All(await _factory.SessionRowsAsync(CookieOf(login)), row => Assert.Null(row.DeviceId));
    }
}
