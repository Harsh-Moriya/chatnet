using System.Net;
using System.Net.Http.Json;
using ChatNET.API.Common.Persistence;
using ChatNET.API.Keys.Persistence;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChatNET.API.Tests.Keys;

// Starting the keys over, for a person who has neither a device nor the passphrase: new keys as the next generation, a new backup,
// every device removed and every conversation marked for a new key period. The account password is asked again.
[Collection(IntegrationTestCollection.Name)]
public class KeyResetTests
{
    private const string DeviceHeader = "X-Device-Id";

    private readonly ChatApiFactory _factory;

    public KeyResetTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response) =>
        (await response.ReadJsonAsync()).TryGetProperty("code", out var code) ? code.GetString() : null;

    private Task<List<int>> GenerationsOfAsync(Guid userId) =>
        _factory.QueryDbAsync(db => db.AccountKeyBundles.Where(b => b.UserId == userId).OrderBy(b => b.Generation).Select(b => b.Generation).ToListAsync());

    // ##### What is stored #####

    [Fact]
    public async Task A_reset_stores_the_next_generation_with_its_new_identity_and_replaces_the_backup()
    {
        var (user, client) = await _factory.UserWithKeysAsync();
        var newIdentity = EncryptionApi.Identity();
        var bundle = EncryptionApi.Bundle(2, newIdentity);
        var backup = EncryptionApi.Backup(length: 220);

        var response = await client.ResetKeysAsync(user.Password, 2, bundle, backup);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.ReadJsonAsync();
        Assert.Equal([1, 2], status.GetProperty("bundles").EnumerateArray().Select(b => b.GetProperty("generation").GetInt32()));
        Assert.Equal(2, status.GetProperty("backup").GetProperty("version").GetInt32());
        var stored = await _factory.QueryDbAsync(db => db.KeyBackups.SingleAsync(b => b.UserId == user.Id));
        Assert.Equal(backup, stored.Blob);

        var other = await _factory.RegisterUserAsync("looker");
        using var otherClient = _factory.CreateClientFor(other);
        var looked = await (await otherClient.LookupKeysAsync([user.Id])).ReadJsonAsync();
        var entry = Assert.Single(looked.GetProperty("bundles").EnumerateArray());
        Assert.Equal(2, entry.GetProperty("generation").GetInt32());
        Assert.Equal(newIdentity, EncryptionApi.FromB64(entry.GetProperty("bundle").GetString()!)[6..38]);
    }

    [Fact]
    public async Task The_backup_is_replaced_whatever_version_it_is_at()
    {
        var (user, client) = await _factory.UserWithKeysAsync();
        await _factory.ExecuteDbAsync(db => db.KeyBackups
            .Where(b => b.UserId == user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Version, 7)));

        var response = await client.ResetKeysAsync(user.Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(8, (await _factory.QueryDbAsync(db => db.KeyBackups.SingleAsync(b => b.UserId == user.Id))).Version);
    }

    [Fact]
    public async Task A_person_whose_backup_is_missing_gets_one_at_version_1()
    {
        var user = await _factory.RegisterUserAsync("nobackup");
        using var client = _factory.CreateClientFor(user);
        var backup = EncryptionApi.Backup(length: 180);

        var response = await client.ResetKeysAsync(user.Password, 2, backup: backup);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await _factory.QueryDbAsync(db => db.KeyBackups.SingleAsync(b => b.UserId == user.Id));
        Assert.Equal(1, stored.Version);
        Assert.Equal(backup, stored.Blob);
    }

    // ##### The password #####

    [Fact]
    public async Task A_wrong_password_is_a_403_with_its_code_and_changes_nothing()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);
        var device = await client.AddDeviceAsync();
        var chat = (await client.CreateDirectAsync(bob.Id)).IdOf();

        var response = await client.ResetKeysAsync("Not-The-Password-1");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("wrong-password", await CodeOfAsync(response));
        Assert.Equal([1], await GenerationsOfAsync(alice.Id));
        Assert.Null((await _factory.QueryDbAsync(db => db.Devices.SingleAsync(d => d.Id == device))).RemovedAt);
        Assert.False((await _factory.QueryDbAsync(db => db.Conversations.SingleAsync(c => c.Id == chat))).KeyRotationRequired);
    }

    [Fact]
    public async Task Wrong_passwords_lock_the_account_and_a_locked_account_refuses_even_the_right_one()
    {
        var (user, client) = await _factory.UserWithKeysAsync();

        for (var attempt = 0; attempt < 5; attempt++)
            Assert.Equal(HttpStatusCode.Forbidden, (await client.ResetKeysAsync("Not-The-Password-1")).StatusCode);

        var locked = await client.ResetKeysAsync(user.Password);

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.NotEmpty(locked.Headers.GetValues("Retry-After"));
        Assert.Equal([1], await GenerationsOfAsync(user.Id));
    }

    [Fact]
    public async Task The_right_password_clears_the_count_of_wrong_ones()
    {
        var (user, client) = await _factory.UserWithKeysAsync();
        for (var attempt = 0; attempt < 4; attempt++)
            await client.ResetKeysAsync("Not-The-Password-1");

        Assert.Equal(HttpStatusCode.OK, (await client.ResetKeysAsync(user.Password)).StatusCode);

        var stored = await _factory.QueryDbAsync(db => db.Users.SingleAsync(u => u.Id == user.Id));
        Assert.Equal(0, stored.AccessFailedCount);
    }

    [Fact]
    public async Task A_deactivated_account_cannot_reset_its_keys()
    {
        var (user, client) = await _factory.UserWithKeysAsync();
        await _factory.ExecuteDbAsync(db => db.Users
            .Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsDeactivated, true)));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.ResetKeysAsync(user.Password)).StatusCode);
        Assert.Equal([1], await GenerationsOfAsync(user.Id));
    }

    // ##### Devices, sessions and conversations #####

    [Fact]
    public async Task Every_device_is_removed_and_its_sessions_end_but_the_session_that_asked_stays()
    {
        var (user, client) = await _factory.UserWithKeysAsync();
        var first = await client.AddDeviceAsync("Web", "Laptop");
        var second = await client.AddDeviceAsync("Ios", "Phone");
        using var plain = _factory.CreateCookielessClient();
        var firstCookie = await SignInOnAsync(plain, user, first);
        var secondCookie = await SignInOnAsync(plain, user, second);

        var response = await client.ResetKeysAsync(user.Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var devices = await _factory.QueryDbAsync(db => db.Devices.Where(d => d.UserId == user.Id).ToListAsync());
        Assert.All(devices, device => Assert.NotNull(device.RemovedAt));
        Assert.Equal(HttpStatusCode.Unauthorized, (await RenewAsync(plain, firstCookie, first)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RenewAsync(plain, secondCookie, second)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/keys/me")).StatusCode);
    }

    [Fact]
    public async Task Devices_of_other_people_are_not_touched()
    {
        var (user, client) = await _factory.UserWithKeysAsync();
        var (_, otherClient) = await _factory.UserWithKeysAsync();
        var theirs = await otherClient.AddDeviceAsync();

        await client.ResetKeysAsync(user.Password);

        Assert.Null((await _factory.QueryDbAsync(db => db.Devices.SingleAsync(d => d.Id == theirs))).RemovedAt);
    }

    [Fact]
    public async Task Every_conversation_the_person_is_in_is_marked_and_announced_once_and_no_other()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var direct = (await aliceClient.CreateDirectAsync(bob.Id)).IdOf();
        var left = (await bobClient.CreateGroupAsync("Left", alice.Id, carol.Id)).IdOf();
        var strangers = (await bobClient.CreateGroupAsync("Strangers", carol.Id)).IdOf();
        await _factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == left && m.UserId == alice.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LeftAt, DateTime.UtcNow)));

        var response = await aliceClient.ResetKeysAsync(alice.Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var due = await _factory.QueryDbAsync(db => db.Conversations
            .Where(c => c.Id == direct || c.Id == left || c.Id == strangers)
            .ToDictionaryAsync(c => c.Id, c => c.KeyRotationRequired));
        Assert.True(due[direct]);
        Assert.False(due[left]);
        Assert.False(due[strangers]);
        Assert.Equal(1, (await _factory.OutboxRowsAboutAsync("conversationId", direct)).Count(r => r.Kind == "ConversationKeyChanged"));
        Assert.Equal(0, (await _factory.OutboxRowsAboutAsync("conversationId", strangers)).Count(r => r.Kind == "ConversationKeyChanged"));
    }

    [Fact]
    public async Task The_next_key_period_must_be_sealed_to_the_new_generation()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var chat = (await aliceClient.CreateDirectAsync(bob.Id)).IdOf();
        await aliceClient.ResetKeysAsync(alice.Password);

        var old = await bobClient.PostEpochAsync(chat, EpochBody.For(2, [(alice.Id, 1), (bob.Id, 1)]));
        var next = await bobClient.PostEpochAsync(chat, EpochBody.For(2, [(alice.Id, 2), (bob.Id, 1)]));

        Assert.Equal(HttpStatusCode.Conflict, old.StatusCode);
        Assert.Equal("epoch-members-changed", await CodeOfAsync(old));
        Assert.Equal(HttpStatusCode.NoContent, next.StatusCode);
    }

    // ##### Taking turns with the key periods #####

    [Fact]
    public async Task A_reset_waits_for_a_key_period_that_is_being_sealed_to_the_old_keys()
    {
        var (user, client) = await _factory.UserWithKeysAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var sealing = await db.Database.BeginTransactionAsync();
        await KeyGenerationLock.HoldSharedAsync(db, [user.Id], CancellationToken.None);

        var request = client.ResetKeysAsync(user.Password);
        await Task.Delay(1500);
        Assert.False(request.IsCompleted);
        await sealing.CommitAsync();

        Assert.Equal(HttpStatusCode.OK, (await request).StatusCode);
    }

    // ##### What is refused #####

    [Fact]
    public async Task A_generation_that_is_not_the_next_is_a_conflict_with_its_code_and_stores_nothing()
    {
        var (user, client) = await _factory.UserWithKeysAsync();

        var response = await client.ResetKeysAsync(user.Password, generation: 3);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("generation-stale", await CodeOfAsync(response));
        Assert.Equal([1], await GenerationsOfAsync(user.Id));
        Assert.Equal(1, (await _factory.QueryDbAsync(db => db.KeyBackups.SingleAsync(b => b.UserId == user.Id))).Version);
    }

    [Fact]
    public async Task Someone_with_no_keys_has_nothing_to_reset()
    {
        var user = await _factory.RegisterUserWithoutKeysAsync("nokeys");
        using var client = _factory.CreateClientFor(user);

        Assert.Equal(HttpStatusCode.NotFound, (await client.ResetKeysAsync(user.Password)).StatusCode);
        Assert.Empty(await GenerationsOfAsync(user.Id));
    }

    [Fact]
    public async Task A_request_that_is_not_well_formed_is_a_400()
    {
        var (user, client) = await _factory.UserWithKeysAsync();

        var badRequests = new[]
        {
            await client.ResetKeysAsync(user.Password, bundle: EncryptionApi.Bundle(2, length: 100)),
            await client.ResetKeysAsync(user.Password, 1),
            await client.ResetKeysAsync(user.Password, backup: EncryptionApi.Backup(length: 10)),
            await client.ResetKeysAsync(null),
            await client.ResetKeysAsync(""),
            await client.ResetKeysAsync(new string('a', 129)),
        };

        Assert.All(badRequests, response => Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode));
        Assert.Equal([1], await GenerationsOfAsync(user.Id));
    }

    // ##### What is logged #####

    [Fact]
    public async Task The_log_names_the_person_and_holds_neither_the_password_nor_key_material()
    {
        var log = new CapturingLoggerProvider();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(log)));
        var user = await _factory.RegisterUserWithoutKeysAsync("resetlog");
        using var client = host.ClientFor(user);
        await client.SetUpKeysAsync();
        var bundle = EncryptionApi.Bundle(2);
        var backup = EncryptionApi.Backup(length: 190);

        await client.ResetKeysAsync("A-Wrong-Password-1", 2, bundle, backup);
        await client.ResetKeysAsync(user.Password, 2, bundle, backup);

        var everything = string.Join('\n', log.Entries);
        Assert.Contains(user.Id.ToString(), everything);
        Assert.DoesNotContain(user.Password, everything);
        Assert.DoesNotContain("A-Wrong-Password-1", everything);
        Assert.DoesNotContain(EncryptionApi.B64(bundle), everything);
        Assert.DoesNotContain(EncryptionApi.B64(backup), everything);
    }

    private static async Task<string> SignInOnAsync(HttpClient plain, TestUser user, Guid device)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email = user.Email, password = user.Password }),
        };
        request.Headers.Add(DeviceHeader, device.ToString());
        var response = await plain.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.GetRefreshCookie()!.Value.ToString();
    }

    private static Task<HttpResponseMessage> RenewAsync(HttpClient plain, string cookie, Guid device)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        request.Headers.Add("Cookie", $"chatnet_refresh={cookie}");
        request.Headers.Add("X-Requested-With", "ChatNET");
        request.Headers.Add(DeviceHeader, device.ToString());
        return plain.SendAsync(request);
    }
}
