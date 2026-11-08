using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ChatNET.API.Tests.Keys;

[Collection(IntegrationTestCollection.Name)]
public class KeyLookupTests
{
    private readonly ChatApiFactory _factory;

    public KeyLookupTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private async Task<(TestUser User, HttpClient Client)> NewUserAsync()
    {
        var user = await _factory.RegisterUserWithoutKeysAsync("lookup");
        return (user, _factory.CreateClientFor(user));
    }

    private static async Task<JsonElement> LookupAsync(HttpClient client, params Guid[] ids) =>
        await (await client.LookupKeysAsync(ids)).ReadJsonAsync();

    [Fact]
    public async Task A_person_gets_the_current_keys_of_the_people_they_name()
    {
        var (alice, aliceClient) = await NewUserAsync();
        var (bob, bobClient) = await NewUserAsync();
        var (_, caller) = await NewUserAsync();
        var aliceBundle = EncryptionApi.Bundle();
        var bobBundle = EncryptionApi.Bundle();
        await aliceClient.SetUpKeysAsync(aliceBundle);
        await bobClient.SetUpKeysAsync(bobBundle);

        var response = await caller.LookupKeysAsync([alice.Id, bob.Id]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bundles = (await response.ReadJsonAsync()).GetProperty("bundles").EnumerateArray().ToList();
        Assert.Equal(2, bundles.Count);
        var byUser = bundles.ToDictionary(b => b.GetProperty("userId").GetGuid());
        Assert.Equal(aliceBundle, EncryptionApi.FromB64(byUser[alice.Id].GetProperty("bundle").GetString()!));
        Assert.Equal(bobBundle, EncryptionApi.FromB64(byUser[bob.Id].GetProperty("bundle").GetString()!));
        Assert.Equal(1, byUser[alice.Id].GetProperty("generation").GetInt32());
    }

    [Fact]
    public async Task Someone_who_has_not_set_up_is_left_out_not_reported_as_an_error()
    {
        var (withKeys, withKeysClient) = await NewUserAsync();
        var (withoutKeys, _) = await NewUserAsync();
        var (_, caller) = await NewUserAsync();
        await withKeysClient.SetUpKeysAsync();

        var body = await LookupAsync(caller, withKeys.Id, withoutKeys.Id, Guid.NewGuid());

        var bundle = Assert.Single(body.GetProperty("bundles").EnumerateArray());
        Assert.Equal(withKeys.Id, bundle.GetProperty("userId").GetGuid());
    }

    [Fact]
    public async Task Only_the_newest_generation_of_each_person_is_offered()
    {
        var (user, client) = await NewUserAsync();
        var (_, caller) = await NewUserAsync();
        await client.SetUpKeysAsync();
        var second = EncryptionApi.Bundle(generation: 2);
        var third = EncryptionApi.Bundle(generation: 3);
        await _factory.ExecuteDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AccountKeyBundles" ("UserId", "Generation", "Bundle", "CreatedAt")
            VALUES ({user.Id}, 2, {second}, now()), ({user.Id}, 3, {third}, now())
            """));

        var body = await LookupAsync(caller, user.Id);

        var bundle = Assert.Single(body.GetProperty("bundles").EnumerateArray());
        Assert.Equal(3, bundle.GetProperty("generation").GetInt32());
        Assert.Equal(third, EncryptionApi.FromB64(bundle.GetProperty("bundle").GetString()!));
    }

    [Fact]
    public async Task Asking_for_a_person_twice_answers_once()
    {
        var (user, client) = await NewUserAsync();
        var (_, caller) = await NewUserAsync();
        await client.SetUpKeysAsync();

        var body = await LookupAsync(caller, user.Id, user.Id, user.Id);

        Assert.Single(body.GetProperty("bundles").EnumerateArray());
    }

    [Fact]
    public async Task A_person_can_look_up_their_own_keys()
    {
        var (user, client) = await NewUserAsync();
        await client.SetUpKeysAsync();

        var body = await LookupAsync(client, user.Id);

        Assert.Single(body.GetProperty("bundles").EnumerateArray());
    }

    // ##### Validation #####

    [Fact]
    public async Task A_full_group_is_the_most_that_can_be_asked_about_at_once()
    {
        var (_, caller) = await NewUserAsync();

        var atTheLimit = await caller.LookupKeysAsync(Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToList());
        var overTheLimit = await caller.LookupKeysAsync(Enumerable.Range(0, 102).Select(_ => Guid.NewGuid()).ToList());

        Assert.Equal(HttpStatusCode.OK, atTheLimit.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, overTheLimit.StatusCode);
        Assert.NotNull((await overTheLimit.ReadProblemAsync()).Errors);
    }

    [Fact]
    public async Task Naming_nobody_is_refused()
    {
        var (_, caller) = await NewUserAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await caller.LookupKeysAsync([])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await caller.PostAsJsonAsync("/api/keys/lookup", new { userIds = (Guid[]?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await caller.PostAsJsonAsync("/api/keys/lookup", new { })).StatusCode);
    }

    [Fact]
    public async Task An_empty_id_is_refused()
    {
        var (_, caller) = await NewUserAsync();

        var response = await caller.LookupKeysAsync([Guid.NewGuid(), Guid.Empty]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Something_that_is_not_an_id_is_refused_without_an_error()
    {
        var (_, caller) = await NewUserAsync();

        var response = await caller.PostAsJsonAsync("/api/keys/lookup", new { userIds = new[] { "not-an-id" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ##### Access #####

    [Fact]
    public async Task Looking_up_needs_a_signed_in_person()
    {
        using var client = _factory.CreateClient();

        var response = await client.LookupKeysAsync([Guid.NewGuid()]);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Looking_up_shares_the_allowance_of_the_other_key_endpoints()
    {
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimiting:Keys:PermitLimit"] = "2",
                ["RateLimiting:Keys:WindowSeconds"] = "60",
            })));
        using var client = host.ClientFor(await _factory.RegisterUserWithoutKeysAsync("limited"));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/keys/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.LookupKeysAsync([Guid.NewGuid()])).StatusCode);

        var refused = await client.LookupKeysAsync([Guid.NewGuid()]);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.InRange(int.Parse(Assert.Single(refused.Headers.GetValues("Retry-After"))), 1, 60);
    }
}
