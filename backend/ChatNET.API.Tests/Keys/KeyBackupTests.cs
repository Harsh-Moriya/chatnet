using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Tests.Keys;

[Collection(IntegrationTestCollection.Name)]
public class KeyBackupTests
{
    private readonly ChatApiFactory _factory;

    public KeyBackupTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Without_a_backup_there_is_nothing_to_fetch_or_replace()
    {
        var user = await _factory.RegisterUserWithoutKeysAsync("nobackup");
        var client = _factory.CreateClientFor(user);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/keys/me/backup")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.ReplaceBackupAsync(EncryptionApi.Backup(), 1)).StatusCode);
    }

    [Fact]
    public async Task Replacing_the_backup_raises_the_version_and_stores_the_new_blob()
    {
        var (_, client) = await _factory.UserWithKeysAsync();
        var replacement = EncryptionApi.Backup(length: 200);

        var response = await client.ReplaceBackupAsync(replacement, expectedVersion: 1);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await response.ReadJsonAsync()).GetProperty("version").GetInt32());

        var fetched = await (await client.GetAsync("/api/keys/me/backup")).ReadJsonAsync();
        Assert.Equal(2, fetched.GetProperty("version").GetInt32());
        Assert.Equal(replacement, EncryptionApi.FromB64(fetched.GetProperty("blob").GetString()!));
    }

    [Fact]
    public async Task A_replacement_made_from_an_old_version_is_a_conflict_and_changes_nothing()
    {
        var (user, client) = await _factory.UserWithKeysAsync();
        var current = EncryptionApi.Backup(length: 150);
        await client.ReplaceBackupAsync(current, 1);

        var stale = await client.ReplaceBackupAsync(EncryptionApi.Backup(length: 99), expectedVersion: 1);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var stored = await _factory.QueryDbAsync(db => db.KeyBackups.SingleAsync(b => b.UserId == user.Id));
        Assert.Equal(2, stored.Version);
        Assert.Equal(current, stored.Blob);
    }

    [Fact]
    public async Task A_version_from_the_future_is_a_conflict_too()
    {
        var (_, client) = await _factory.UserWithKeysAsync();

        Assert.Equal(HttpStatusCode.Conflict, (await client.ReplaceBackupAsync(EncryptionApi.Backup(), expectedVersion: 5)).StatusCode);
    }

    // Eight devices changing the passphrase from the same version: one conditional statement lets one through.
    [Fact]
    public async Task Of_devices_changing_the_passphrase_together_exactly_one_wins()
    {
        var (user, client) = await _factory.UserWithKeysAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => client.ReplaceBackupAsync(EncryptionApi.Backup(length: 100 + i), 1)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(2, (await _factory.QueryDbAsync(db => db.KeyBackups.SingleAsync(b => b.UserId == user.Id))).Version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_missing_or_impossible_version_is_refused(int? version)
    {
        var (_, client) = await _factory.UserWithKeysAsync();

        var response = await client.ReplaceBackupAsync(EncryptionApi.Backup(), version);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_blob_that_is_not_a_backup_is_refused_and_the_old_one_stays()
    {
        var (user, client) = await _factory.UserWithKeysAsync();
        var before = (await _factory.QueryDbAsync(db => db.KeyBackups.SingleAsync(b => b.UserId == user.Id))).Blob;

        foreach (var bad in new[] { EncryptionApi.Backup(version: 2), EncryptionApi.Backup(length: 35), EncryptionApi.Backup(length: 64 * 1024 + 1), [] })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.ReplaceBackupAsync(bad, 1)).StatusCode);

        var after = await _factory.QueryDbAsync(db => db.KeyBackups.SingleAsync(b => b.UserId == user.Id));
        Assert.Equal(before, after.Blob);
        Assert.Equal(1, after.Version);
    }

    // The backup is the one thing that can be attacked offline, so it is only ever the caller's own, and the route has no id.
    [Fact]
    public async Task Each_person_fetches_and_replaces_only_their_own_backup()
    {
        var (_, alice) = await _factory.UserWithKeysAsync();
        var (bobUser, bob) = await _factory.UserWithKeysAsync();
        var aliceBlob = EncryptionApi.Backup(length: 140);
        await alice.ReplaceBackupAsync(aliceBlob, 1);
        var bobBlob = (await _factory.QueryDbAsync(db => db.KeyBackups.SingleAsync(b => b.UserId == bobUser.Id))).Blob;

        var seenByBob = await (await bob.GetAsync("/api/keys/me/backup")).ReadJsonAsync();
        await bob.ReplaceBackupAsync(EncryptionApi.Backup(length: 90), 1);
        var aliceNow = await (await alice.GetAsync("/api/keys/me/backup")).ReadJsonAsync();

        Assert.Equal(bobBlob, EncryptionApi.FromB64(seenByBob.GetProperty("blob").GetString()!));
        Assert.Equal(aliceBlob, EncryptionApi.FromB64(aliceNow.GetProperty("blob").GetString()!));
        Assert.Equal(2, aliceNow.GetProperty("version").GetInt32());
    }
}
