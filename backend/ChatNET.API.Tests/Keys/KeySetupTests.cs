using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Tests.Keys;

[Collection(IntegrationTestCollection.Name)]
public class KeySetupTests
{
    private readonly ChatApiFactory _factory;

    public KeySetupTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private async Task<(TestUser User, HttpClient Client)> NewUserAsync()
    {
        var user = await _factory.RegisterUserAsync("setup");
        return (user, _factory.CreateClientFor(user));
    }

    [Fact]
    public async Task A_person_who_has_not_set_up_has_no_keys_and_no_backup()
    {
        var (_, client) = await NewUserAsync();

        var response = await client.GetAsync("/api/keys/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal(0, body.GetProperty("bundles").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("backup").ValueKind);
    }

    [Fact]
    public async Task Setting_up_stores_the_bundle_and_the_backup_and_answers_with_them()
    {
        var (_, client) = await NewUserAsync();
        var bundle = EncryptionApi.Bundle();
        var backup = EncryptionApi.Backup();

        var created = await client.SetUpKeysAsync(bundle, backup);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("/api/keys/me", created.Headers.Location?.ToString());
        var body = await created.ReadJsonAsync();
        var stored = Assert.Single(body.GetProperty("bundles").EnumerateArray());
        Assert.Equal(1, stored.GetProperty("generation").GetInt32());
        Assert.Equal(bundle, EncryptionApi.FromB64(stored.GetProperty("bundle").GetString()!));
        Assert.Equal(1, body.GetProperty("backup").GetProperty("version").GetInt32());

        // Reading it again gives the same.
        var read = await (await client.GetAsync("/api/keys/me")).ReadJsonAsync();
        Assert.Equal(bundle, EncryptionApi.FromB64(read.GetProperty("bundles")[0].GetProperty("bundle").GetString()!));
        Assert.Equal(1, read.GetProperty("backup").GetProperty("version").GetInt32());
        Assert.Equal(
            body.GetProperty("backup").GetProperty("updatedAt").GetDateTime(),
            read.GetProperty("backup").GetProperty("updatedAt").GetDateTime());

        var backupBody = await (await client.GetAsync("/api/keys/me/backup")).ReadJsonAsync();
        Assert.Equal(backup, EncryptionApi.FromB64(backupBody.GetProperty("blob").GetString()!));
    }

    [Fact]
    public async Task Setting_up_a_second_time_is_a_conflict_and_changes_nothing()
    {
        var (user, client) = await NewUserAsync();
        var first = EncryptionApi.Bundle();
        await client.SetUpKeysAsync(first);

        var again = await client.SetUpKeysAsync();

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(409, (await again.ReadProblemAsync()).Status);
        var stored = await _factory.QueryDbAsync(db => db.AccountKeyBundles.Where(b => b.UserId == user.Id).ToListAsync());
        Assert.Equal(first, Assert.Single(stored).Bundle);
    }

    // Eight devices of one person setting up together: the key of the table lets one in.
    [Fact]
    public async Task Setting_up_together_stores_one_set_and_refuses_the_rest()
    {
        var (user, client) = await NewUserAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.SetUpKeysAsync()));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, await _factory.QueryDbAsync(db => db.AccountKeyBundles.CountAsync(b => b.UserId == user.Id)));
        Assert.Equal(1, await _factory.QueryDbAsync(db => db.KeyBackups.CountAsync(b => b.UserId == user.Id)));
    }

    // The bundle and the backup are stored together or not at all. A backup row that already exists makes the second insert fail,
    // and the bundle inserted just before it must not stay.
    [Fact]
    public async Task A_failure_storing_the_backup_leaves_no_bundle_behind()
    {
        var (user, client) = await NewUserAsync();
        await _factory.ExecuteDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "KeyBackups" ("UserId", "Blob", "Version", "UpdatedAt") VALUES ({user.Id}, {EncryptionApi.Backup()}, 1, now())
            """));

        var response = await client.SetUpKeysAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(0, await _factory.QueryDbAsync(db => db.AccountKeyBundles.CountAsync(b => b.UserId == user.Id)));
    }

    [Fact]
    public async Task Two_people_have_keys_of_their_own()
    {
        var (_, alice) = await NewUserAsync();
        var (_, bob) = await NewUserAsync();
        var aliceBundle = EncryptionApi.Bundle();
        var bobBundle = EncryptionApi.Bundle();
        await alice.SetUpKeysAsync(aliceBundle);
        await bob.SetUpKeysAsync(bobBundle);

        var seenByAlice = await (await alice.GetAsync("/api/keys/me")).ReadJsonAsync();
        var seenByBob = await (await bob.GetAsync("/api/keys/me")).ReadJsonAsync();

        Assert.Equal(aliceBundle, EncryptionApi.FromB64(seenByAlice.GetProperty("bundles")[0].GetProperty("bundle").GetString()!));
        Assert.Equal(bobBundle, EncryptionApi.FromB64(seenByBob.GetProperty("bundles")[0].GetProperty("bundle").GetString()!));
    }

    // ##### Validation #####

    public static TheoryData<string, byte[]> BadBundles => new()
    {
        { "a bundle one byte short", EncryptionApi.Bundle(length: 133) },
        { "a bundle one byte long", EncryptionApi.Bundle(length: 135) },
        { "an unknown version", EncryptionApi.Bundle(version: 2) },
        { "an unknown suite", EncryptionApi.Bundle(suite: 2) },
        { "generation 0", EncryptionApi.Bundle(generation: 0) },
        { "generation 2 for a first set-up", EncryptionApi.Bundle(generation: 2) },
        { "nothing", [] },
    };

    [Theory]
    [MemberData(nameof(BadBundles))]
    public async Task A_bundle_that_is_not_a_first_generation_bundle_is_refused(string _, byte[] bundle)
    {
        var (user, client) = await NewUserAsync();

        var response = await client.PostAsJsonAsync("/api/keys/me", new { bundle = EncryptionApi.B64(bundle), backup = EncryptionApi.B64(EncryptionApi.Backup()) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull((await response.ReadProblemAsync()).Errors);
        Assert.Equal(0, await _factory.QueryDbAsync(db => db.AccountKeyBundles.CountAsync(b => b.UserId == user.Id)));
    }

    public static TheoryData<string, byte[]> BadBackups => new()
    {
        { "an unknown version", EncryptionApi.Backup(version: 2) },
        { "an unknown suite", EncryptionApi.Backup(suite: 2) },
        { "an unknown key derivation", EncryptionApi.Backup(kdf: 2) },
        { "a header with no ciphertext", EncryptionApi.Backup(length: 35) },
        { "a header and less than a tag", EncryptionApi.Backup(length: 50) },
        { "a header and only a tag", EncryptionApi.Backup(length: 51) },
        { "a backup over the limit", EncryptionApi.Backup(length: 64 * 1024 + 1) },
        { "nothing", [] },
    };

    [Theory]
    [MemberData(nameof(BadBackups))]
    public async Task A_backup_that_is_not_an_encrypted_backup_is_refused(string _, byte[] backup)
    {
        var (user, client) = await NewUserAsync();

        var response = await client.PostAsJsonAsync("/api/keys/me", new { bundle = EncryptionApi.B64(EncryptionApi.Bundle()), backup = EncryptionApi.B64(backup) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await _factory.QueryDbAsync(db => db.AccountKeyBundles.CountAsync(b => b.UserId == user.Id)));
    }

    [Fact]
    public async Task The_smallest_and_largest_backups_are_accepted()
    {
        var (_, small) = await NewUserAsync();
        var (_, large) = await NewUserAsync();

        Assert.Equal(HttpStatusCode.Created, (await small.SetUpKeysAsync(backup: EncryptionApi.Backup(length: 52))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await large.SetUpKeysAsync(backup: EncryptionApi.Backup(length: 64 * 1024))).StatusCode);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("not base64url!", "also not")]
    [InlineData("AAAA+/==", "AAAA+/==")]
    public async Task Missing_or_malformed_values_are_refused_without_an_error(string? bundle, string? backup)
    {
        var (_, client) = await NewUserAsync();

        var response = await client.PostAsJsonAsync("/api/keys/me", new { bundle, backup });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_answer_to_a_refused_value_never_repeats_it()
    {
        var (_, client) = await NewUserAsync();
        var bundle = EncryptionApi.B64(EncryptionApi.Bundle(version: 9));

        var response = await client.PostAsJsonAsync("/api/keys/me", new { bundle, backup = EncryptionApi.B64(EncryptionApi.Backup()) });

        Assert.DoesNotContain(bundle, await response.Content.ReadAsStringAsync());
    }

    // ##### Access #####

    [Theory]
    [InlineData("GET", "/api/keys/me")]
    [InlineData("POST", "/api/keys/me")]
    [InlineData("GET", "/api/keys/me/backup")]
    [InlineData("PUT", "/api/keys/me/backup")]
    public async Task Every_endpoint_needs_a_signed_in_person(string method, string path)
    {
        using var client = _factory.CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
