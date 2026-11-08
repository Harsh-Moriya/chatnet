using System.Net;
using System.Net.Http.Json;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ChatNET.API.Tests.Keys;

[Collection(IntegrationTestCollection.Name)]
public class EncryptionLimitsAndLogsTests
{
    private readonly ChatApiFactory _factory;

    public EncryptionLimitsAndLogsTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // ##### The limit on asking #####

    [Fact]
    public async Task Asking_beyond_the_limit_is_refused_with_the_wait_for_keys_devices_and_links_alike()
    {
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimiting:Keys:PermitLimit"] = "4",
                ["RateLimiting:Keys:WindowSeconds"] = "60",
            })));
        var user = await _factory.RegisterUserAsync("limited");
        using var client = host.ClientFor(user);

        // Four requests across the three groups of endpoints share one allowance.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/keys/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/devices/link-requests/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/keys/me/backup")).StatusCode);

        var refused = await client.GetAsync("/api/keys/me");

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("application/problem+json", refused.Content.Headers.ContentType?.MediaType);
        Assert.InRange(int.Parse(Assert.Single(refused.Headers.GetValues("Retry-After"))), 1, 60);
    }

    [Fact]
    public async Task The_limit_counts_each_person_apart()
    {
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimiting:Keys:PermitLimit"] = "1",
                ["RateLimiting:Keys:WindowSeconds"] = "60",
            })));
        using var alice = host.ClientFor(await _factory.RegisterUserAsync("a"));
        using var bob = host.ClientFor(await _factory.RegisterUserAsync("b"));

        await alice.GetAsync("/api/keys/me");

        Assert.Equal(HttpStatusCode.TooManyRequests, (await alice.GetAsync("/api/keys/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/api/keys/me")).StatusCode);
    }

    [Fact]
    public async Task A_request_that_is_refused_is_not_carried_out()
    {
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimiting:Keys:PermitLimit"] = "1",
                ["RateLimiting:Keys:WindowSeconds"] = "60",
            })));
        var user = await _factory.RegisterUserAsync("refused");
        using var client = host.ClientFor(user);
        await client.GetAsync("/api/keys/me");

        var refused = await client.SetUpKeysAsync();

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        var status = await (await _factory.CreateClientFor(user).GetAsync("/api/keys/me")).ReadJsonAsync();
        Assert.Equal(0, status.GetProperty("bundles").GetArrayLength());
    }

    // ##### What the log holds #####

    // Keys, the backup, the sealed secrets, the key and code of a link request and the name of a device never reach the log, whether
    // as a message, a structured value or an exception.
    [Fact]
    public async Task Nothing_secret_reaches_the_log_through_the_whole_life_of_keys_devices_and_links()
    {
        var log = new CapturingLoggerProvider();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(log)));
        var user = await _factory.RegisterUserAsync("logged");
        using var client = host.ClientFor(user);

        var bundle = EncryptionApi.Bundle();
        var backup = EncryptionApi.Backup(length: 150);
        var replacement = EncryptionApi.Backup(length: 160);
        var temporaryKey = EncryptionApi.Identity();
        var code = EncryptionApi.NewCode();
        var payload = EncryptionApi.SealedBox(200);
        const string deviceName = "Quite-A-Distinctive-Device-Name";

        await client.SetUpKeysAsync(bundle, backup);
        await client.GetAsync("/api/keys/me/backup");
        await client.ReplaceBackupAsync(replacement, 1);
        var device = await client.AddDeviceAsync("Web", deviceName);
        var link = await client.OpenedLinkIdAsync(code, temporaryKey);
        await client.FindLinkAsync(code);
        await client.ApproveLinkAsync(link, payload);
        await client.PollLinkAsync(link);
        await client.DeleteAsync($"/api/devices/{device}");
        // And some refusals, which log too.
        await client.SetUpKeysAsync(bundle, backup);
        await client.OpenLinkAsync(code, temporaryKey);
        await client.ApproveLinkAsync(link, payload);

        var everything = string.Join('\n', log.Entries);
        Assert.NotEmpty(log.Entries);
        foreach (var secret in new[]
                 {
                     EncryptionApi.B64(bundle), EncryptionApi.B64(backup), EncryptionApi.B64(replacement), EncryptionApi.B64(temporaryKey),
                     EncryptionApi.B64(payload), code, deviceName,
                 })
            Assert.DoesNotContain(secret, everything);
    }

    // The ids and the facts are logged, so an operator can follow what happened.
    [Fact]
    public async Task The_log_names_the_person_and_the_device_but_not_what_they_hold()
    {
        var log = new CapturingLoggerProvider();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(log)));
        var user = await _factory.RegisterUserAsync("named");
        using var client = host.ClientFor(user);
        await client.SetUpKeysAsync();

        var device = await client.AddDeviceAsync();
        await client.DeleteAsync($"/api/devices/{device}");

        var everything = string.Join('\n', log.Entries);
        Assert.Contains(user.Id.ToString(), everything);
        Assert.Contains(device.ToString(), everything);
    }
}
