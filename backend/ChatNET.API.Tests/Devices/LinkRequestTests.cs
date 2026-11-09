using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ChatNET.API.Devices.Persistence;
using ChatNET.API.Devices.Linking;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ChatNET.API.Tests.Devices;

// A new device asks to be given the keys; a signed-in device finds the request by its code and approves it with the secrets sealed
// to the key of the request; the new device collects them once.
[Collection(IntegrationTestCollection.Name)]
public class LinkRequestTests
{
    private readonly ChatApiFactory _factory;

    public LinkRequestTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private static async Task<string> StateAsync(HttpClient client, Guid id) =>
        (await (await client.PollLinkAsync(id)).ReadJsonAsync()).GetProperty("state").GetString()!;

    // ##### Opening a request #####

    [Fact]
    public async Task A_person_with_no_keys_cannot_open_a_request()
    {
        var user = await _factory.RegisterUserWithoutKeysAsync("nolink");
        var client = _factory.CreateClientFor(user);

        var response = await client.OpenLinkAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_request_is_stored_with_its_key_and_expires_after_the_configured_time_in_whole_seconds()
    {
        var clock = new ManualTimeProvider();
        using var host = _factory.WithClock(clock);
        var user = await _factory.RegisterUserWithoutKeysAsync("open");
        using var client = host.ClientFor(user);
        await client.SetUpKeysAsync();
        var key = RandomNumberGenerator.GetBytes(32);
        var code = EncryptionApi.NewCode();

        var response = await client.OpenLinkAsync(code, key, "Android", "  Pixel  ");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.ReadJsonAsync();
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal($"/api/devices/link-requests/{id}", response.Headers.Location?.ToString());
        var expiresAt = body.GetProperty("expiresAt").GetDateTime();
        Assert.Equal(0, expiresAt.Ticks % TimeSpan.TicksPerSecond);
        Assert.Equal(clock.GetUtcNow().UtcDateTime.AddMinutes(10), expiresAt, TimeSpan.FromSeconds(1));

        var stored = await _factory.QueryDbAsync(db => db.DeviceLinkRequests.SingleAsync(r => r.Id == id));
        Assert.Equal(user.Id, stored.UserId);
        Assert.Equal(code, stored.Code);
        Assert.Equal(key, stored.TemporaryPublicKey);
        Assert.Equal("Pixel", stored.Name);
        Assert.Null(stored.Payload);
    }

    [Fact]
    public async Task The_lifetime_of_a_request_is_configurable()
    {
        using var host = _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["Encryption:LinkLifetime"] = "00:02:00" })));
        var user = await _factory.RegisterUserWithoutKeysAsync("short");
        using var client = host.ClientFor(user);
        await client.SetUpKeysAsync();

        var body = await (await client.OpenLinkAsync()).ReadJsonAsync();

        Assert.Equal(DateTime.UtcNow.AddMinutes(2), body.GetProperty("expiresAt").GetDateTime(), TimeSpan.FromSeconds(30));
    }

    public static TheoryData<string, string?, string?, string?, string?> BadRequests => new()
    {
        { "no key", null, "0123456789", "Web", "n" },
        { "a key of 31 bytes", EncryptionApi.B64(new byte[31]), "0123456789", "Web", "n" },
        { "a key of 33 bytes", EncryptionApi.B64(new byte[33]), "0123456789", "Web", "n" },
        { "a key that is not base64url", "not base64!", "0123456789", "Web", "n" },
        { "no code", EncryptionApi.B64(new byte[32]), null, "Web", "n" },
        { "a code of 9 characters", EncryptionApi.B64(new byte[32]), "012345678", "Web", "n" },
        { "a code of 11 characters", EncryptionApi.B64(new byte[32]), "01234567890", "Web", "n" },
        { "a lowercase code", EncryptionApi.B64(new byte[32]), "abcdefghjk", "Web", "n" },
        { "a code with the letter I", EncryptionApi.B64(new byte[32]), "012345678I", "Web", "n" },
        { "a code with the letter L", EncryptionApi.B64(new byte[32]), "012345678L", "Web", "n" },
        { "a code with the letter O", EncryptionApi.B64(new byte[32]), "012345678O", "Web", "n" },
        { "a code with the letter U", EncryptionApi.B64(new byte[32]), "012345678U", "Web", "n" },
        { "a code with a hyphen", EncryptionApi.B64(new byte[32]), "01234-5678", "Web", "n" },
        { "no kind", EncryptionApi.B64(new byte[32]), "0123456789", null, "n" },
        { "an unknown kind", EncryptionApi.B64(new byte[32]), "0123456789", "Tablet", "n" },
        { "no name", EncryptionApi.B64(new byte[32]), "0123456789", "Web", null },
        { "a name of spaces", EncryptionApi.B64(new byte[32]), "0123456789", "Web", "   " },
    };

    [Theory]
    [MemberData(nameof(BadRequests))]
    public async Task A_request_that_is_not_well_formed_is_refused(string _, string? key, string? code, string? kind, string? name)
    {
        var (user, client) = await _factory.UserWithKeysAsync();

        var response = await client.PostAsJsonAsync("/api/devices/link-requests", new { temporaryPublicKey = key, code, kind, name });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await _factory.QueryDbAsync(db => db.DeviceLinkRequests.CountAsync(r => r.UserId == user.Id)));
    }

    [Fact]
    public void Every_character_of_the_alphabet_is_a_valid_code_character()
    {
        Assert.All(EncryptionApi.CodeAlphabet, character => Assert.True(LinkCodes.IsCode(new string(character, 10))));
        Assert.Equal(10, DeviceLinkRequestConfiguration.CodeLength);
        Assert.False(LinkCodes.IsCode(null));
    }

    [Fact]
    public async Task A_code_already_open_for_the_same_person_is_a_conflict_but_another_person_may_use_it()
    {
        var (_, alice) = await _factory.UserWithKeysAsync();
        var (_, bob) = await _factory.UserWithKeysAsync();
        const string code = "ABCDEFGHJK";
        await alice.OpenLinkAsync(code);

        Assert.Equal(HttpStatusCode.Conflict, (await alice.OpenLinkAsync(code)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await bob.OpenLinkAsync(code)).StatusCode);
    }

    // ##### The limit of open requests #####

    [Fact]
    public async Task A_person_has_at_most_the_configured_number_waiting_and_an_approved_one_makes_room()
    {
        using var host = _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["Encryption:MaxPendingLinkRequests"] = "2" })));
        var (user, _) = await _factory.UserWithKeysAsync();
        using var client = host.ClientFor(user);
        var first = await client.OpenedLinkIdAsync();
        await client.OpenedLinkIdAsync();

        Assert.Equal(HttpStatusCode.Conflict, (await client.OpenLinkAsync()).StatusCode);

        await client.ApproveLinkAsync(first);

        Assert.Equal(HttpStatusCode.Created, (await client.OpenLinkAsync()).StatusCode);
    }

    [Fact]
    public async Task Requests_opened_together_never_exceed_the_limit()
    {
        using var host = _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["Encryption:MaxPendingLinkRequests"] = "3" })));
        var (user, _) = await _factory.UserWithKeysAsync();
        using var client = host.ClientFor(user);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.OpenLinkAsync()));

        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Requests_that_expired_do_not_count_and_long_expired_ones_are_removed_when_a_new_one_opens()
    {
        var clock = new ManualTimeProvider();
        using var host = _factory.WithClock(clock);
        var user = await _factory.RegisterUserWithoutKeysAsync("stale");
        using var client = host.ClientFor(user);
        await client.SetUpKeysAsync();
        var old = await client.OpenedLinkIdAsync();
        for (var i = 0; i < 4; i++)
            await client.OpenLinkAsync();

        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(HttpStatusCode.Created, (await client.OpenLinkAsync()).StatusCode);
        // Expired but not yet an hour old: still stored.
        Assert.True(await _factory.QueryDbAsync(db => db.DeviceLinkRequests.AnyAsync(r => r.Id == old)));

        clock.Advance(TimeSpan.FromHours(2));
        await client.OpenLinkAsync();

        Assert.False(await _factory.QueryDbAsync(db => db.DeviceLinkRequests.AnyAsync(r => r.Id == old)));
    }

    // ##### Finding a request by its code #####

    [Fact]
    public async Task The_signed_in_device_finds_the_request_by_its_code_and_is_shown_what_asks()
    {
        var (_, client) = await _factory.UserWithKeysAsync();
        var key = RandomNumberGenerator.GetBytes(32);
        var code = EncryptionApi.NewCode();
        var id = await client.OpenedLinkIdAsync(code, key);

        var response = await client.FindLinkAsync(code);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal(id, body.GetProperty("id").GetGuid());
        Assert.Equal(key, EncryptionApi.FromB64(body.GetProperty("temporaryPublicKey").GetString()!));
        Assert.Equal("Web", body.GetProperty("kind").GetString());
        Assert.Equal("New browser", body.GetProperty("name").GetString());
        Assert.Equal(0, body.GetProperty("expiresAt").GetDateTime().Ticks % TimeSpan.TicksPerSecond);
    }

    [Fact]
    public async Task A_code_that_belongs_to_nobody_expired_or_approved_is_not_found()
    {
        var clock = new ManualTimeProvider();
        using var host = _factory.WithClock(clock);
        var user = await _factory.RegisterUserWithoutKeysAsync("find");
        using var client = host.ClientFor(user);
        await client.SetUpKeysAsync();
        var approved = EncryptionApi.NewCode();
        var expiring = EncryptionApi.NewCode();
        await client.ApproveLinkAsync(await client.OpenedLinkIdAsync(approved));
        await client.OpenLinkAsync(expiring);

        Assert.Equal(HttpStatusCode.NotFound, (await client.FindLinkAsync(EncryptionApi.NewCode())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.FindLinkAsync(approved)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.FindLinkAsync(expiring)).StatusCode);

        clock.Advance(TimeSpan.FromMinutes(11));

        Assert.Equal(HttpStatusCode.NotFound, (await client.FindLinkAsync(expiring)).StatusCode);
    }

    [Fact]
    public async Task A_code_opened_by_one_person_is_not_found_by_another()
    {
        var (_, alice) = await _factory.UserWithKeysAsync();
        var (_, bob) = await _factory.UserWithKeysAsync();
        var code = EncryptionApi.NewCode();
        await alice.OpenLinkAsync(code);

        Assert.Equal(HttpStatusCode.NotFound, (await bob.FindLinkAsync(code)).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("abcdefghjk")]
    [InlineData("0123456789A")]
    public async Task A_code_that_is_not_a_code_is_refused(string code)
    {
        var (_, client) = await _factory.UserWithKeysAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.FindLinkAsync(code)).StatusCode);
    }

    [Fact]
    public async Task Asking_with_no_code_at_all_is_refused()
    {
        var (_, client) = await _factory.UserWithKeysAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/devices/link-requests")).StatusCode);
    }

    // ##### Approving and collecting #####

    [Fact]
    public async Task A_new_device_waits_then_collects_the_sealed_secrets_once()
    {
        var (_, client) = await _factory.UserWithKeysAsync();
        var id = await client.OpenedLinkIdAsync();
        var sealedSecrets = EncryptionApi.SealedBox(300);

        var waiting = await (await client.PollLinkAsync(id)).ReadJsonAsync();
        Assert.Equal("Pending", waiting.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, waiting.GetProperty("payload").ValueKind);

        Assert.Equal(HttpStatusCode.NoContent, (await client.ApproveLinkAsync(id, sealedSecrets)).StatusCode);

        var collected = await (await client.PollLinkAsync(id)).ReadJsonAsync();
        Assert.Equal("Approved", collected.GetProperty("state").GetString());
        Assert.Equal(sealedSecrets, EncryptionApi.FromB64(collected.GetProperty("payload").GetString()!));

        var again = await (await client.PollLinkAsync(id)).ReadJsonAsync();
        Assert.Equal("Collected", again.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, again.GetProperty("payload").ValueKind);
    }

    [Fact]
    public async Task Collecting_clears_the_sealed_secrets_from_the_database()
    {
        var (_, client) = await _factory.UserWithKeysAsync();
        var id = await client.OpenedLinkIdAsync();
        await client.ApproveLinkAsync(id);
        Assert.NotNull((await _factory.QueryDbAsync(db => db.DeviceLinkRequests.SingleAsync(r => r.Id == id))).Payload);

        await client.PollLinkAsync(id);

        var stored = await _factory.QueryDbAsync(db => db.DeviceLinkRequests.SingleAsync(r => r.Id == id));
        Assert.Null(stored.Payload);
        Assert.NotNull(stored.ApprovedAt);
        Assert.NotNull(stored.CollectedAt);
    }

    // Eight polls of one approved request together: the statement that reads the bytes also clears them, so one gets them.
    [Fact]
    public async Task Of_devices_collecting_together_exactly_one_gets_the_secrets()
    {
        var (_, client) = await _factory.UserWithKeysAsync();
        var id = await client.OpenedLinkIdAsync();
        await client.ApproveLinkAsync(id);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.PollLinkAsync(id)));
        var bodies = await Task.WhenAll(responses.Select(r => r.ReadJsonAsync()));

        Assert.Equal(1, bodies.Count(b => b.GetProperty("state").GetString() == "Approved"));
        Assert.Equal(7, bodies.Count(b => b.GetProperty("state").GetString() == "Collected"));
        Assert.Single(bodies, b => b.GetProperty("payload").ValueKind == JsonValueKind.String);
    }

    [Fact]
    public async Task A_request_is_approved_once_and_a_second_approval_is_a_conflict_that_keeps_the_first()
    {
        var (_, client) = await _factory.UserWithKeysAsync();
        var id = await client.OpenedLinkIdAsync();
        var first = EncryptionApi.SealedBox(100);
        await client.ApproveLinkAsync(id, first);

        var second = await client.ApproveLinkAsync(id, EncryptionApi.SealedBox(200));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = await (await client.PollLinkAsync(id)).ReadJsonAsync();
        Assert.Equal(first, EncryptionApi.FromB64(body.GetProperty("payload").GetString()!));
    }

    [Fact]
    public async Task Of_devices_approving_together_exactly_one_wins()
    {
        var (_, client) = await _factory.UserWithKeysAsync();
        var id = await client.OpenedLinkIdAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.ApproveLinkAsync(id)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task A_request_cannot_be_approved_after_it_expired_and_is_reported_expired()
    {
        var clock = new ManualTimeProvider();
        using var host = _factory.WithClock(clock);
        var user = await _factory.RegisterUserWithoutKeysAsync("late");
        using var client = host.ClientFor(user);
        await client.SetUpKeysAsync();
        var id = await client.OpenedLinkIdAsync();

        clock.Advance(TimeSpan.FromMinutes(11));

        Assert.Equal(HttpStatusCode.Conflict, (await client.ApproveLinkAsync(id)).StatusCode);
        Assert.Equal("Expired", await StateAsync(client, id));
    }

    [Fact]
    public async Task Secrets_that_were_approved_but_not_collected_in_time_are_never_handed_over()
    {
        var clock = new ManualTimeProvider();
        using var host = _factory.WithClock(clock);
        var user = await _factory.RegisterUserWithoutKeysAsync("slow");
        using var client = host.ClientFor(user);
        await client.SetUpKeysAsync();
        var id = await client.OpenedLinkIdAsync();
        await client.ApproveLinkAsync(id);

        clock.Advance(TimeSpan.FromMinutes(11));
        var late = await (await client.PollLinkAsync(id)).ReadJsonAsync();

        Assert.Equal("Expired", late.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, late.GetProperty("payload").ValueKind);
    }

    [Fact]
    public async Task A_request_that_nobody_approved_is_reported_expired_after_its_time()
    {
        var clock = new ManualTimeProvider();
        using var host = _factory.WithClock(clock);
        var user = await _factory.RegisterUserWithoutKeysAsync("nobody");
        using var client = host.ClientFor(user);
        await client.SetUpKeysAsync();
        var id = await client.OpenedLinkIdAsync();
        Assert.Equal("Pending", await StateAsync(client, id));

        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal("Pending", await StateAsync(client, id));

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("Expired", await StateAsync(client, id));
    }

    // ##### Whose it is #####

    [Fact]
    public async Task Another_person_can_neither_see_approve_nor_collect_a_request()
    {
        var (_, alice) = await _factory.UserWithKeysAsync();
        var (_, bob) = await _factory.UserWithKeysAsync();
        var id = await alice.OpenedLinkIdAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await bob.PollLinkAsync(id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.ApproveLinkAsync(id)).StatusCode);

        // And the request is untouched: Alice can still approve it.
        Assert.Equal(HttpStatusCode.NoContent, (await alice.ApproveLinkAsync(id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PollLinkAsync(id)).StatusCode);
    }

    [Fact]
    public async Task An_unknown_request_is_not_found()
    {
        var (_, client) = await _factory.UserWithKeysAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await client.PollLinkAsync(Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.ApproveLinkAsync(Guid.NewGuid())).StatusCode);
    }

    public static TheoryData<string, byte[]> BadPayloads => new()
    {
        { "nothing", [] },
        { "no more than a box header", EncryptionApi.SealedBox(62) },
        { "an unknown version", EncryptionApi.SealedBox(100, version: 2) },
        { "an unknown suite", EncryptionApi.SealedBox(100, suite: 2) },
        { "more than the limit", EncryptionApi.SealedBox(64 * 1024 + 1) },
    };

    [Theory]
    [MemberData(nameof(BadPayloads))]
    public async Task A_payload_that_is_not_a_sealed_box_is_refused_and_the_request_stays_open(string _, byte[] payload)
    {
        var (_, client) = await _factory.UserWithKeysAsync();
        var id = await client.OpenedLinkIdAsync();

        var response = await client.ApproveLinkAsync(id, payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Pending", await StateAsync(client, id));
    }

    [Fact]
    public async Task The_largest_payload_is_accepted()
    {
        var (_, client) = await _factory.UserWithKeysAsync();
        var id = await client.OpenedLinkIdAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await client.ApproveLinkAsync(id, EncryptionApi.SealedBox(64 * 1024))).StatusCode);
    }
}
