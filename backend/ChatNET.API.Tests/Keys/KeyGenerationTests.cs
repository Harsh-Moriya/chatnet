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

// A new generation of the account key, which is how a lost device is cut off from what is sealed from then on: the bundle, the
// backup that holds its private half, the removal of the device and the marking of the person's conversations are one step.
[Collection(IntegrationTestCollection.Name)]
public class KeyGenerationTests
{
    private const string DeviceHeader = "X-Device-Id";

    private readonly ChatApiFactory _factory;

    public KeyGenerationTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // A person as the other tests make them (keys on the server) with the backup a real set-up leaves too, which a new generation replaces.
    private async Task<TestUser> PersonAsync(string prefix)
    {
        var user = await _factory.RegisterUserAsync(prefix);
        var backup = EncryptionApi.Backup(length: 140);
        await _factory.ExecuteDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "KeyBackups" ("UserId", "Blob", "Version", "UpdatedAt") VALUES ({user.Id}, {backup}, 1, now())
            """));
        return user;
    }

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response) =>
        (await response.ReadJsonAsync()).TryGetProperty("code", out var code) ? code.GetString() : null;

    private Task<List<int>> GenerationsOfAsync(Guid userId) =>
        _factory.QueryDbAsync(db => db.AccountKeyBundles.Where(b => b.UserId == userId).OrderBy(b => b.Generation).Select(b => b.Generation).ToListAsync());

    private Task<Dictionary<Guid, bool>> DueAsync(params Guid[] conversationIds) =>
        _factory.QueryDbAsync(db => db.Conversations
            .Where(c => conversationIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.KeyRotationRequired));

    private async Task<int> KeyChangedRowsAsync(Guid conversationId) =>
        (await _factory.OutboxRowsAboutAsync("conversationId", conversationId)).Count(r => r.Kind == "ConversationKeyChanged");

    // ##### What is stored #####

    [Fact]
    public async Task The_next_generation_is_stored_beside_the_old_one_and_the_backup_is_replaced()
    {
        var (user, client) = await _factory.UserWithKeysAsync();
        var bundle = EncryptionApi.Bundle(2);
        var backup = EncryptionApi.Backup(length: 200);

        var response = await client.AddKeyGenerationAsync(2, 1, bundle: bundle, backup: backup);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.ReadJsonAsync();
        Assert.Equal([1, 2], status.GetProperty("bundles").EnumerateArray().Select(b => b.GetProperty("generation").GetInt32()));
        Assert.Equal(2, status.GetProperty("backup").GetProperty("version").GetInt32());

        Assert.Equal([1, 2], await GenerationsOfAsync(user.Id));
        var stored = await _factory.QueryDbAsync(db => db.KeyBackups.SingleAsync(b => b.UserId == user.Id));
        Assert.Equal(2, stored.Version);
        Assert.Equal(backup, stored.Blob);
        var newest = await _factory.QueryDbAsync(db => db.AccountKeyBundles.SingleAsync(b => b.UserId == user.Id && b.Generation == 2));
        Assert.Equal(bundle, newest.Bundle);
    }

    [Fact]
    public async Task People_who_look_up_the_keys_are_given_the_new_generation()
    {
        var (user, client) = await _factory.UserWithKeysAsync();
        var other = await _factory.RegisterUserAsync("looker");
        using var otherClient = _factory.CreateClientFor(other);
        var bundle = EncryptionApi.Bundle(2);
        await client.AddKeyGenerationAsync(2, 1, bundle: bundle);

        var found = await (await otherClient.LookupKeysAsync([user.Id])).ReadJsonAsync();

        var entry = Assert.Single(found.GetProperty("bundles").EnumerateArray());
        Assert.Equal(2, entry.GetProperty("generation").GetInt32());
        Assert.Equal(bundle, EncryptionApi.FromB64(entry.GetProperty("bundle").GetString()!));
    }

    [Fact]
    public async Task Generations_follow_one_another_from_the_backup_version_that_was_read()
    {
        var (user, client) = await _factory.UserWithKeysAsync();

        Assert.Equal(HttpStatusCode.OK, (await client.AddKeyGenerationAsync(2, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.AddKeyGenerationAsync(3, 2)).StatusCode);

        Assert.Equal([1, 2, 3], await GenerationsOfAsync(user.Id));
        Assert.Equal(3, (await _factory.QueryDbAsync(db => db.KeyBackups.SingleAsync(b => b.UserId == user.Id))).Version);
    }

    // ##### What is refused #####

    [Fact]
    public async Task A_generation_that_is_not_the_next_is_a_conflict_with_its_code_and_stores_nothing()
    {
        var (user, client) = await _factory.UserWithKeysAsync();
        await client.AddKeyGenerationAsync(2, 1);

        var again = await client.AddKeyGenerationAsync(2, 2);
        var skipped = await client.AddKeyGenerationAsync(4, 2);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("generation-stale", await CodeOfAsync(again));
        Assert.Equal(HttpStatusCode.Conflict, skipped.StatusCode);
        Assert.Equal("generation-stale", await CodeOfAsync(skipped));
        Assert.Equal([1, 2], await GenerationsOfAsync(user.Id));
        Assert.Equal(2, (await _factory.QueryDbAsync(db => db.KeyBackups.SingleAsync(b => b.UserId == user.Id))).Version);
    }

    [Fact]
    public async Task A_backup_that_moved_on_refuses_the_whole_change_and_leaves_the_device_and_the_conversations_alone()
    {
        var alice = await PersonAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);
        var device = await client.AddDeviceAsync();
        var chat = (await client.CreateDirectAsync(bob.Id)).IdOf();
        await client.ReplaceBackupAsync(EncryptionApi.Backup(length: 130), 1);

        var response = await client.AddKeyGenerationAsync(2, expectedBackupVersion: 1, removeDeviceId: device);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("backup-version-stale", await CodeOfAsync(response));
        Assert.Equal([1], await GenerationsOfAsync(alice.Id));
        Assert.Null((await _factory.QueryDbAsync(db => db.Devices.SingleAsync(d => d.Id == device))).RemovedAt);
        Assert.False((await DueAsync(chat))[chat]);
        Assert.Equal(0, await KeyChangedRowsAsync(chat));
    }

    [Fact]
    public async Task A_device_that_is_not_the_callers_is_a_404_and_changes_nothing()
    {
        var alice = await PersonAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var bobsDevice = await bobClient.AddDeviceAsync();

        var response = await aliceClient.AddKeyGenerationAsync(2, 1, removeDeviceId: bobsDevice);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal([1], await GenerationsOfAsync(alice.Id));
        Assert.Equal(1, (await _factory.QueryDbAsync(db => db.KeyBackups.SingleAsync(b => b.UserId == alice.Id))).Version);
        Assert.Null((await _factory.QueryDbAsync(db => db.Devices.SingleAsync(d => d.Id == bobsDevice))).RemovedAt);
    }

    [Fact]
    public async Task A_device_that_was_already_removed_is_a_404_and_changes_nothing()
    {
        var (user, client) = await _factory.UserWithKeysAsync();
        var device = await client.AddDeviceAsync();
        await client.DeleteAsync($"/api/devices/{device}");

        var response = await client.AddKeyGenerationAsync(2, 1, removeDeviceId: device);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal([1], await GenerationsOfAsync(user.Id));
    }

    [Fact]
    public async Task Someone_with_no_keys_has_no_generation_to_follow()
    {
        var user = await _factory.RegisterUserWithoutKeysAsync("nokeys");
        using var client = _factory.CreateClientFor(user);

        Assert.Equal(HttpStatusCode.NotFound, (await client.AddKeyGenerationAsync(2, 1)).StatusCode);
        Assert.Empty(await GenerationsOfAsync(user.Id));
    }

    [Fact]
    public async Task A_request_that_is_not_well_formed_is_a_400()
    {
        var (_, client) = await _factory.UserWithKeysAsync();

        var badRequests = new[]
        {
            await client.AddKeyGenerationAsync(2, 1, bundle: EncryptionApi.Bundle(2, length: 100)),
            await client.AddKeyGenerationAsync(2, 1, bundle: EncryptionApi.Bundle(2, version: 2)),
            await client.AddKeyGenerationAsync(2, 1, bundle: EncryptionApi.Bundle(2, suite: 9)),
            await client.AddKeyGenerationAsync(1, 1),
            await client.AddKeyGenerationAsync(2, 1, backup: EncryptionApi.Backup(length: 10)),
            await client.AddKeyGenerationAsync(2, 1, backup: EncryptionApi.Backup(kdf: 9)),
            await client.AddKeyGenerationAsync(2, null),
            await client.AddKeyGenerationAsync(2, 0),
            await client.PostAsJsonAsync("/api/keys/me/generations", new { backup = EncryptionApi.B64(EncryptionApi.Backup()), expectedBackupVersion = 1 }),
        };

        Assert.All(badRequests, response => Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode));
    }

    // ##### The device that was lost #####

    [Fact]
    public async Task The_device_is_removed_and_its_sessions_end_while_the_other_devices_keep_theirs()
    {
        var (user, client) = await _factory.UserWithKeysAsync();
        var lost = await client.AddDeviceAsync("Ios", "Lost phone");
        var kept = await client.AddDeviceAsync("Web", "Laptop");
        using var plain = _factory.CreateCookielessClient();
        var lostCookie = await SignInOnAsync(plain, user, lost);
        var keptCookie = await SignInOnAsync(plain, user, kept);

        var response = await client.AddKeyGenerationAsync(2, 1, removeDeviceId: lost);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull((await _factory.QueryDbAsync(db => db.Devices.SingleAsync(d => d.Id == lost))).RemovedAt);
        Assert.Null((await _factory.QueryDbAsync(db => db.Devices.SingleAsync(d => d.Id == kept))).RemovedAt);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RenewAsync(plain, lostCookie, lost)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RenewAsync(plain, keptCookie, kept)).StatusCode);
    }

    [Fact]
    public async Task A_new_generation_without_a_lost_device_removes_none()
    {
        var (_, client) = await _factory.UserWithKeysAsync();
        var device = await client.AddDeviceAsync();

        Assert.Equal(HttpStatusCode.OK, (await client.AddKeyGenerationAsync(2, 1)).StatusCode);

        Assert.Null((await _factory.QueryDbAsync(db => db.Devices.SingleAsync(d => d.Id == device))).RemovedAt);
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

    // ##### The conversations #####

    [Fact]
    public async Task Every_conversation_the_person_is_in_is_marked_for_a_new_key_period_and_announced_once_and_no_other()
    {
        var alice = await PersonAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var direct = (await aliceClient.CreateDirectAsync(bob.Id)).IdOf();
        var team = (await aliceClient.CreateGroupAsync("Team", bob.Id)).IdOf();
        var left = (await bobClient.CreateGroupAsync("Left", alice.Id, carol.Id)).IdOf();
        var strangers = (await bobClient.CreateGroupAsync("Strangers", carol.Id)).IdOf();
        await _factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == left && m.UserId == alice.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LeftAt, DateTime.UtcNow)));

        var response = await aliceClient.AddKeyGenerationAsync(2, 1);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var due = await DueAsync(direct, team, left, strangers);
        Assert.True(due[direct]);
        Assert.True(due[team]);
        Assert.False(due[left]);
        Assert.False(due[strangers]);
        Assert.Equal(1, await KeyChangedRowsAsync(direct));
        Assert.Equal(1, await KeyChangedRowsAsync(team));
        Assert.Equal(0, await KeyChangedRowsAsync(left));
        Assert.Equal(0, await KeyChangedRowsAsync(strangers));
    }

    [Fact]
    public async Task The_next_key_period_must_be_sealed_to_the_new_generation_and_one_sealed_to_the_old_is_refused()
    {
        var alice = await PersonAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var team = (await aliceClient.CreateGroupAsync("Team", bob.Id)).IdOf();
        await aliceClient.AddKeyGenerationAsync(2, 1);

        var old = await bobClient.PostEpochAsync(team, EpochBody.For(2, [(alice.Id, 1), (bob.Id, 1)]));
        var next = await bobClient.PostEpochAsync(team, EpochBody.For(2, [(alice.Id, 2), (bob.Id, 1)]));

        Assert.Equal(HttpStatusCode.Conflict, old.StatusCode);
        Assert.Equal("epoch-members-changed", await CodeOfAsync(old));
        Assert.Equal(HttpStatusCode.NoContent, next.StatusCode);
        Assert.False((await DueAsync(team))[team]);
    }

    [Fact]
    public async Task A_new_key_period_can_be_made_at_once_because_the_conversation_is_due_even_when_the_last_was_just_made()
    {
        var alice = await PersonAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var chat = (await aliceClient.CreateDirectAsync(bob.Id)).IdOf();
        await aliceClient.AddKeyGenerationAsync(2, 1);

        Assert.Equal(HttpStatusCode.NoContent, (await bobClient.PostEpochAsync(chat, EpochBody.For(2, [(alice.Id, 2), (bob.Id, 1)]))).StatusCode);
    }

    // ##### Taking turns with the key periods #####

    // The lock a generation is stored under is held here as an epoch being sealed would hold it. Each request that seals a key to the
    // person must wait for it and then read the generation as it is after the generation was stored.

    private async Task<(Guid Chat, TestUser Alice, TestUser Bob)> DueGroupAsync()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);
        var team = (await client.CreateGroupAsync("Team", bob.Id)).IdOf();
        await _factory.ExecuteDbAsync(db => db.Conversations
            .Where(c => c.Id == team)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.KeyRotationRequired, true)));
        return (team, alice, bob);
    }

    private static async Task StoreGenerationAsync(AppDbContext db, Guid userId, int generation) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AccountKeyBundles" ("UserId", "Generation", "Bundle", "CreatedAt")
            VALUES ({userId}, {generation}, {EncryptionApi.Bundle(generation)}, {DateTime.UtcNow})
            """);

    [Fact]
    public async Task A_new_key_period_waits_for_a_generation_that_is_being_stored_and_is_refused_when_it_was_made_for_the_old_one()
    {
        var (team, alice, bob) = await DueGroupAsync();
        using var client = _factory.CreateClientFor(alice);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var storing = await db.Database.BeginTransactionAsync();
        await KeyGenerationLock.HoldExclusiveAsync(db, bob.Id, CancellationToken.None);

        var request = client.PostEpochAsync(team, EpochBody.For(2, [(alice.Id, 1), (bob.Id, 1)]));
        await Task.Delay(1500);
        Assert.False(request.IsCompleted);
        await StoreGenerationAsync(db, bob.Id, 2);
        await storing.CommitAsync();

        var response = await request;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("epoch-members-changed", await CodeOfAsync(response));
    }

    [Fact]
    public async Task Adding_people_waits_for_a_generation_that_is_being_stored_and_is_refused_when_it_was_made_for_the_old_one()
    {
        var (team, alice, bob) = await DueGroupAsync();
        var carol = await _factory.RegisterUserAsync("carol");
        using var client = _factory.CreateClientFor(alice);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var storing = await db.Database.BeginTransactionAsync();
        await KeyGenerationLock.HoldExclusiveAsync(db, carol.Id, CancellationToken.None);

        var request = client.AddMembersResponseAsync(team, carol.Id);
        await Task.Delay(1500);
        Assert.False(request.IsCompleted);
        await StoreGenerationAsync(db, carol.Id, 2);
        await storing.CommitAsync();

        var response = await request;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("epoch-members-changed", await CodeOfAsync(response));
        Assert.DoesNotContain(await _factory.QueryDbAsync(db2 => db2.ConversationMembers.Where(m => m.ConversationId == team).Select(m => m.UserId).ToListAsync()), id => id == carol.Id);
    }

    [Fact]
    public async Task A_new_group_waits_for_a_generation_that_is_being_stored_and_is_not_saved_when_it_was_made_for_the_old_one()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var storing = await db.Database.BeginTransactionAsync();
        await KeyGenerationLock.HoldExclusiveAsync(db, bob.Id, CancellationToken.None);

        var request = client.PostGroupAsync("Late", bob.Id);
        await Task.Delay(1500);
        Assert.False(request.IsCompleted);
        await StoreGenerationAsync(db, bob.Id, 2);
        await storing.CommitAsync();

        var response = await request;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("epoch-members-changed", await CodeOfAsync(response));
        Assert.Empty(await client.ListConversationsAsync());
    }

    [Fact]
    public async Task A_new_direct_chat_waits_for_a_generation_that_is_being_stored_and_is_not_saved_when_it_was_made_for_the_old_one()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var storing = await db.Database.BeginTransactionAsync();
        await KeyGenerationLock.HoldExclusiveAsync(db, bob.Id, CancellationToken.None);

        var request = client.PostDirectAsync(bob.Id);
        await Task.Delay(1500);
        Assert.False(request.IsCompleted);
        await StoreGenerationAsync(db, bob.Id, 2);
        await storing.CommitAsync();

        var response = await request;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("epoch-members-changed", await CodeOfAsync(response));
        Assert.Empty(await client.ListConversationsAsync());
    }

    [Fact]
    public async Task A_new_generation_waits_for_a_key_period_that_is_being_sealed_to_the_old_one()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await PersonAsync("bob");
        using var bobClient = _factory.CreateClientFor(bob);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var sealing = await db.Database.BeginTransactionAsync();
        await KeyGenerationLock.HoldSharedAsync(db, [alice.Id, bob.Id], CancellationToken.None);

        var request = bobClient.AddKeyGenerationAsync(2, 1);
        await Task.Delay(1500);
        Assert.False(request.IsCompleted);
        await sealing.CommitAsync();

        Assert.Equal(HttpStatusCode.OK, (await request).StatusCode);
    }

    [Fact]
    public async Task A_key_period_for_other_people_does_not_wait_for_a_generation_that_is_being_stored()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var client = _factory.CreateClientFor(alice);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var storing = await db.Database.BeginTransactionAsync();
        await KeyGenerationLock.HoldExclusiveAsync(db, carol.Id, CancellationToken.None);

        var response = await client.PostGroupAsync("Not carol", bob.Id);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await storing.RollbackAsync();
    }

    // ##### What is logged #####

    [Fact]
    public async Task The_log_names_the_person_the_generation_and_the_device_and_holds_no_key_material()
    {
        var log = new CapturingLoggerProvider();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(log)));
        var user = await _factory.RegisterUserWithoutKeysAsync("generation");
        using var client = host.ClientFor(user);
        await client.SetUpKeysAsync();
        var device = await client.AddDeviceAsync("Web", "A-Quite-Distinctive-Lost-Device");
        var bundle = EncryptionApi.Bundle(2);
        var backup = EncryptionApi.Backup(length: 170);

        await client.AddKeyGenerationAsync(2, 1, removeDeviceId: device, bundle: bundle, backup: backup);
        await client.AddKeyGenerationAsync(2, 2, bundle: bundle, backup: backup);

        var everything = string.Join('\n', log.Entries);
        Assert.Contains(user.Id.ToString(), everything);
        Assert.Contains(device.ToString(), everything);
        Assert.DoesNotContain(EncryptionApi.B64(bundle), everything);
        Assert.DoesNotContain(EncryptionApi.B64(backup), everything);
        Assert.DoesNotContain("A-Quite-Distinctive-Lost-Device", everything);
    }
}
