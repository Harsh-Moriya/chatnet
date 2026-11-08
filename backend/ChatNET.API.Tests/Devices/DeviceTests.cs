using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ChatNET.API.Tests.Devices;

[Collection(IntegrationTestCollection.Name)]
public class DeviceTests
{
    private readonly ChatApiFactory _factory;

    public DeviceTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private WebApplicationFactory<Program> HostWith(Dictionary<string, string?> settings) =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings)));

    private async Task<List<JsonElement>> ListAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/devices");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.ReadJsonAsync()).EnumerateArray().ToList();
    }

    // ##### Registering #####

    [Fact]
    public async Task A_device_cannot_be_added_before_encryption_is_set_up()
    {
        var user = await _factory.RegisterUserAsync("early");
        var client = _factory.CreateClientFor(user);

        var response = await client.RegisterDeviceAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, await _factory.QueryDbAsync(db => db.Devices.CountAsync(d => d.UserId == user.Id)));
    }

    [Fact]
    public async Task A_device_is_added_and_appears_in_the_list()
    {
        var (user, client) = await _factory.UserWithKeysAsync();

        var response = await client.RegisterDeviceAsync("Ios", "Harsh's phone");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.ReadJsonAsync();
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal($"/api/devices/{id}", response.Headers.Location?.ToString());
        Assert.Equal("Ios", created.GetProperty("kind").GetString());
        Assert.Equal("Harsh's phone", created.GetProperty("name").GetString());
        Assert.Equal(created.GetProperty("createdAt").GetDateTime(), created.GetProperty("lastActiveAt").GetDateTime());

        var listed = Assert.Single(await ListAsync(client));
        Assert.Equal(id, listed.GetProperty("id").GetGuid());
        Assert.Equal(created.GetProperty("createdAt").GetDateTime(), listed.GetProperty("createdAt").GetDateTime());
        var stored = await _factory.QueryDbAsync(db => db.Devices.SingleAsync(d => d.Id == id));
        Assert.Equal(user.Id, stored.UserId);
        Assert.Null(stored.RemovedAt);
    }

    [Fact]
    public async Task The_kind_is_read_whatever_its_case_and_the_name_is_stored_trimmed()
    {
        var (_, client) = await _factory.UserWithKeysAsync();

        var response = await client.RegisterDeviceAsync("android", "  Pixel  ");

        var created = await response.ReadJsonAsync();
        Assert.Equal("Android", created.GetProperty("kind").GetString());
        Assert.Equal("Pixel", created.GetProperty("name").GetString());
    }

    [Theory]
    [InlineData(null, "name")]
    [InlineData("", "name")]
    [InlineData("Desktop", "name")]
    [InlineData("1", "name")]
    [InlineData("-1", "name")]
    [InlineData("Web", null)]
    [InlineData("Web", "")]
    [InlineData("Web", "   ")]
    [InlineData("Web", "bad\u0007name")]
    [InlineData("Web", "line\nbreak")]
    public async Task A_kind_or_name_that_is_not_one_is_refused(string? kind, string? name)
    {
        var (user, client) = await _factory.UserWithKeysAsync();

        var response = await client.PostAsJsonAsync("/api/devices", new { kind, name });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await _factory.QueryDbAsync(db => db.Devices.CountAsync(d => d.UserId == user.Id)));
    }

    [Fact]
    public async Task A_name_of_exactly_one_hundred_characters_is_kept_and_one_more_is_refused()
    {
        var (_, client) = await _factory.UserWithKeysAsync();

        Assert.Equal(HttpStatusCode.Created, (await client.RegisterDeviceAsync("Web", new string('n', 100))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.RegisterDeviceAsync("Web", new string('n', 101))).StatusCode);
    }

    // ##### The limit #####

    [Fact]
    public async Task A_person_has_at_most_the_configured_number_of_devices_and_removing_one_makes_room()
    {
        using var host = HostWith(new() { ["Encryption:MaxDevicesPerUser"] = "2" });
        var (user, _) = await _factory.UserWithKeysAsync();
        using var client = host.ClientFor(user);
        var first = await client.AddDeviceAsync();
        await client.AddDeviceAsync();

        var third = await client.RegisterDeviceAsync();

        Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);
        Assert.Equal(2, (await ListAsync(client)).Count);

        await client.DeleteAsync($"/api/devices/{first}");

        Assert.Equal(HttpStatusCode.Created, (await client.RegisterDeviceAsync()).StatusCode);
    }

    // Devices of one person registering together at the limit: the lock on the person makes the count and the insert one step.
    [Fact]
    public async Task Devices_registered_together_never_exceed_the_limit()
    {
        using var host = HostWith(new() { ["Encryption:MaxDevicesPerUser"] = "3" });
        var (user, _) = await _factory.UserWithKeysAsync();
        using var client = host.ClientFor(user);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.RegisterDeviceAsync()));

        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(3, await _factory.QueryDbAsync(db => db.Devices.CountAsync(d => d.UserId == user.Id)));
    }

    [Fact]
    public async Task The_limit_is_per_person()
    {
        using var host = HostWith(new() { ["Encryption:MaxDevicesPerUser"] = "1" });
        var (alice, _) = await _factory.UserWithKeysAsync();
        var (bob, _) = await _factory.UserWithKeysAsync();
        using var aliceClient = host.ClientFor(alice);
        using var bobClient = host.ClientFor(bob);

        Assert.Equal(HttpStatusCode.Created, (await aliceClient.RegisterDeviceAsync()).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await bobClient.RegisterDeviceAsync()).StatusCode);
    }

    // ##### Listing and removing #####

    [Fact]
    public async Task The_list_holds_only_the_callers_devices_that_are_not_removed_oldest_first()
    {
        var (_, alice) = await _factory.UserWithKeysAsync();
        var (_, bob) = await _factory.UserWithKeysAsync();
        var first = await alice.AddDeviceAsync("Web", "first");
        var removed = await alice.AddDeviceAsync("Web", "removed");
        var second = await alice.AddDeviceAsync("Ios", "second");
        await bob.AddDeviceAsync("Web", "bob's");
        await alice.DeleteAsync($"/api/devices/{removed}");

        var listed = await ListAsync(alice);

        Assert.Equal([first, second], listed.Select(d => d.GetProperty("id").GetGuid()).ToList());
    }

    [Fact]
    public async Task Removing_a_device_hides_it_and_a_second_removal_is_not_found()
    {
        var (user, client) = await _factory.UserWithKeysAsync();
        var id = await client.AddDeviceAsync();

        var removed = await client.DeleteAsync($"/api/devices/{id}");
        var again = await client.DeleteAsync($"/api/devices/{id}");

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Empty(await ListAsync(client));
        // The row stays, marked, so a device told it was removed can find that out.
        Assert.NotNull((await _factory.QueryDbAsync(db => db.Devices.SingleAsync(d => d.Id == id))).RemovedAt);
        Assert.Equal(user.Id, (await _factory.QueryDbAsync(db => db.Devices.SingleAsync(d => d.Id == id))).UserId);
    }

    [Fact]
    public async Task Another_persons_device_and_an_unknown_one_are_both_not_found_and_stay_untouched()
    {
        var (_, alice) = await _factory.UserWithKeysAsync();
        var (_, bob) = await _factory.UserWithKeysAsync();
        var alices = await alice.AddDeviceAsync();

        var byBob = await bob.DeleteAsync($"/api/devices/{alices}");
        var unknown = await bob.DeleteAsync($"/api/devices/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, byBob.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Single(await ListAsync(alice));
    }

    [Fact]
    public async Task Removing_a_device_removes_it_once_when_asked_together()
    {
        var (_, client) = await _factory.UserWithKeysAsync();
        var id = await client.AddDeviceAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => client.DeleteAsync($"/api/devices/{id}")));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.NotFound));
    }

    [Theory]
    [InlineData("GET", "/api/devices")]
    [InlineData("POST", "/api/devices")]
    [InlineData("DELETE", "/api/devices/3f2a9c1e-7b4d-4e8a-9c55-0a1b2c3d4e5f")]
    [InlineData("POST", "/api/devices/link-requests")]
    [InlineData("GET", "/api/devices/link-requests?code=0123456789")]
    [InlineData("GET", "/api/devices/link-requests/3f2a9c1e-7b4d-4e8a-9c55-0a1b2c3d4e5f")]
    [InlineData("POST", "/api/devices/link-requests/3f2a9c1e-7b4d-4e8a-9c55-0a1b2c3d4e5f/approve")]
    public async Task Every_endpoint_needs_a_signed_in_person(string method, string path)
    {
        using var client = _factory.CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
