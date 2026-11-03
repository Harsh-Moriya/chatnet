using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Tests.Push;

// Registering a browser for alerts, and taking it off again. The shared host has no push keys (push is off); the tests of what
// a host does with them build one with keys.
[Collection(IntegrationTestCollection.Name)]
public class PushSubscriptionEndpointTests
{
    private readonly ChatApiFactory _factory;

    public PushSubscriptionEndpointTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // ##### The key #####

    [Fact]
    public async Task The_key_is_given_to_a_signed_in_person_when_push_is_set_up()
    {
        var alice = await _factory.RegisterUserAsync("push");
        using var host = _factory.WithPush();
        using var client = host.ClientFor(alice);

        var response = await client.GetAsync("/api/push/key");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(PushApi.Vapid.PublicKey, body.GetProperty("publicKey").GetString());

        // Only the public half: the private key is what signs a push and never leaves the server.
        Assert.DoesNotContain(PushApi.Vapid.PrivateKey, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task There_is_no_key_when_push_is_not_set_up()
    {
        var alice = await _factory.RegisterUserAsync("push");
        using var client = _factory.CreateClientFor(alice);

        var response = await client.GetAsync("/api/push/key");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.ReadProblemAsync();
        Assert.Equal("Push notifications are not set up on this server.", problem.Detail);
    }

    [Fact]
    public async Task The_key_needs_a_token()
    {
        using var host = _factory.WithPush();
        using var anonymous = host.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/push/key")).StatusCode);
    }

    // ##### Registering a browser #####

    [Fact]
    public async Task A_browser_is_registered_for_the_person_who_sent_it()
    {
        var alice = await _factory.RegisterUserAsync("push");
        var browser = Browser.Subscribe();
        using var host = _factory.WithPush();
        using var client = host.ClientFor(alice);

        var response = await client.PutSubscriptionAsync(browser);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var stored = Assert.Single(await _factory.SubscriptionsOfAsync(alice.Id));
        Assert.Equal(browser.Endpoint, stored.Endpoint);
        Assert.Equal(browser.P256dh, stored.P256dh);
        Assert.Equal(browser.Auth, stored.Auth);
        Assert.Equal(stored.CreatedAt, stored.LastUsedAt);
    }

    // The page registers every time it opens, so a second request is the normal case, and it renews the registration.
    [Fact]
    public async Task Registering_the_same_browser_again_keeps_one_row_and_renews_it()
    {
        var alice = await _factory.RegisterUserAsync("push");
        var browser = Browser.Subscribe();
        using var host = _factory.WithPush();
        using var client = host.ClientFor(alice);
        await client.PutSubscriptionAsync(browser);
        var first = Assert.Single(await _factory.SubscriptionsOfAsync(alice.Id));
        await Task.Delay(50);

        var response = await client.PutSubscriptionAsync(browser);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var again = Assert.Single(await _factory.SubscriptionsOfAsync(alice.Id));
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(first.CreatedAt, again.CreatedAt);
        Assert.True(again.LastUsedAt > first.LastUsedAt);
    }

    [Fact]
    public async Task A_browser_that_made_new_keys_replaces_the_old_ones_on_its_row()
    {
        var alice = await _factory.RegisterUserAsync("push");
        var browser = Browser.Subscribe();
        using var host = _factory.WithPush();
        using var client = host.ClientFor(alice);
        await client.PutSubscriptionAsync(browser);

        var renewed = Browser.Subscribe();
        var response = await client.PutAsJsonAsync("/api/push/subscriptions", new
        {
            endpoint = browser.Endpoint,
            keys = new { p256dh = renewed.P256dh, auth = renewed.Auth },
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var stored = Assert.Single(await _factory.SubscriptionsOfAsync(alice.Id));
        Assert.Equal(renewed.P256dh, stored.P256dh);
        Assert.Equal(renewed.Auth, stored.Auth);
    }

    // Two people sign in on one browser one after the other. The second registers the endpoint the first left, and it is theirs
    // from then on: a browser is one row, and an alert for the first person must not appear on a screen the second is using.
    [Fact]
    public async Task Another_person_registering_the_same_browser_takes_it_over()
    {
        var alice = await _factory.RegisterUserAsync("push");
        var bob = await _factory.RegisterUserAsync("push");
        var browser = Browser.Subscribe();
        using var host = _factory.WithPush();
        using var aliceClient = host.ClientFor(alice);
        using var bobClient = host.ClientFor(bob);
        await aliceClient.PutSubscriptionAsync(browser);

        await bobClient.PutSubscriptionAsync(browser);

        Assert.Empty(await _factory.SubscriptionsOfAsync(alice.Id));
        Assert.Single(await _factory.SubscriptionsOfAsync(bob.Id));
        Assert.Single(await _factory.SubscriptionsAtAsync(browser.Endpoint));
    }

    [Fact]
    public async Task Registrations_that_arrive_together_leave_one_row()
    {
        var alice = await _factory.RegisterUserAsync("push");
        var browser = Browser.Subscribe();
        using var host = _factory.WithPush();
        using var client = host.ClientFor(alice);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.PutSubscriptionAsync(browser)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
        Assert.Single(await _factory.SubscriptionsAtAsync(browser.Endpoint));
    }

    [Fact]
    public async Task A_person_keeps_at_most_the_limit_of_browsers_and_loses_the_one_used_least_recently()
    {
        var alice = await _factory.RegisterUserAsync("push");
        var older = Browser.Subscribe();
        var newer = Browser.Subscribe();
        var newest = Browser.Subscribe();
        await _factory.AddSubscriptionAsync(alice.Id, older, lastUsedAt: DateTime.UtcNow.AddDays(-3));
        await _factory.AddSubscriptionAsync(alice.Id, newer, lastUsedAt: DateTime.UtcNow.AddDays(-1));
        using var host = _factory.WithPush(settings: new Dictionary<string, string?> { ["Push:MaxSubscriptionsPerUser"] = "2" });
        using var client = host.ClientFor(alice);

        await client.PutSubscriptionAsync(newest);

        var kept = (await _factory.SubscriptionsOfAsync(alice.Id)).Select(s => s.Endpoint).ToList();
        Assert.Equal(2, kept.Count);
        Assert.Contains(newest.Endpoint, kept);
        Assert.Contains(newer.Endpoint, kept);
        Assert.DoesNotContain(older.Endpoint, kept);
    }

    [Fact]
    public async Task The_limit_is_per_person()
    {
        var alice = await _factory.RegisterUserAsync("push");
        var bob = await _factory.RegisterUserAsync("push");
        await _factory.AddSubscriptionAsync(bob.Id, Browser.Subscribe());
        await _factory.AddSubscriptionAsync(alice.Id, Browser.Subscribe());
        using var host = _factory.WithPush(settings: new Dictionary<string, string?> { ["Push:MaxSubscriptionsPerUser"] = "1" });
        using var client = host.ClientFor(alice);

        await client.PutSubscriptionAsync(Browser.Subscribe());

        Assert.Single(await _factory.SubscriptionsOfAsync(alice.Id));
        Assert.Single(await _factory.SubscriptionsOfAsync(bob.Id));
    }

    [Fact]
    public async Task A_registration_is_refused_when_push_is_not_set_up_and_nothing_is_kept()
    {
        var alice = await _factory.RegisterUserAsync("push");
        using var client = _factory.CreateClientFor(alice);

        var response = await client.PutSubscriptionAsync(Browser.Subscribe());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await _factory.SubscriptionsOfAsync(alice.Id));
    }

    [Fact]
    public async Task Registering_needs_a_token()
    {
        using var host = _factory.WithPush();
        using var anonymous = host.CreateClient();

        var response = await anonymous.PutSubscriptionAsync(Browser.Subscribe());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ##### What is refused #####

    public static IEnumerable<object[]> Refused()
    {
        var good = Browser.Subscribe();

        yield return ["an address of a host that is not a push service", "https://evil.example/fcm/send/abc", good.P256dh, good.Auth, "endpoint"];
        yield return ["an address on this machine", "https://127.0.0.1/fcm/send/abc", good.P256dh, good.Auth, "endpoint"];
        yield return ["an address that is not https", "http://fcm.googleapis.com/fcm/send/abc", good.P256dh, good.Auth, "endpoint"];
        yield return ["a host that only starts with an allowed one", "https://fcm.googleapis.com.evil.example/x", good.P256dh, good.Auth, "endpoint"];
        yield return ["no address", "", good.P256dh, good.Auth, "endpoint"];
        yield return ["an address that is too long", "https://fcm.googleapis.com/" + new string('a', 2100), good.P256dh, good.Auth, "endpoint"];
        yield return ["a public key that is not base64url", good.Endpoint, "not base64 at all!", good.Auth, "p256dh"];
        yield return ["a public key of the right length made of characters that are not base64url", good.Endpoint, new string('!', 87), good.Auth, "p256dh"];
        yield return ["a public key of the wrong length", good.Endpoint, "AAAA", good.Auth, "p256dh"];
        yield return ["a public key that is not an uncompressed point", good.Endpoint, Convert.ToBase64String(new byte[65]).Replace('+', '-').Replace('/', '_').TrimEnd('='), good.Auth, "p256dh"];
        yield return ["no public key", good.Endpoint, "", good.Auth, "p256dh"];
        yield return ["a public key that is one byte too long", good.Endpoint, Base64Url.EncodeToString([0x04, .. new byte[65]]), good.Auth, "p256dh"];
        yield return ["a secret of the right length made of characters that are not base64url", good.Endpoint, good.P256dh, new string('#', 22), "auth"];
        yield return ["a secret of the wrong length", good.Endpoint, good.P256dh, "AAAA", "auth"];
        yield return ["a secret that is one byte too long", good.Endpoint, good.P256dh, Base64Url.EncodeToString(new byte[17]), "auth"];
        yield return ["a secret that is not base64url", good.Endpoint, good.P256dh, "###", "auth"];
        yield return ["no secret", good.Endpoint, good.P256dh, "", "auth"];
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task A_registration_that_is_not_valid_is_a_400_that_names_the_field_and_stores_nothing(
        string because, string endpoint, string p256dh, string auth, string field)
    {
        var alice = await _factory.RegisterUserAsync("push");
        using var host = _factory.WithPush();
        using var client = host.ClientFor(alice);

        var response = await client.PutAsJsonAsync("/api/push/subscriptions", new { endpoint, keys = new { p256dh, auth } });

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, because);
        var problem = await response.ReadProblemAsync();
        Assert.Contains(problem.Errors!.Value.EnumerateObject(), e => string.Equals(e.Name, field, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(await _factory.SubscriptionsOfAsync(alice.Id));
    }

    [Fact]
    public async Task A_refusal_never_repeats_the_address()
    {
        var alice = await _factory.RegisterUserAsync("push");
        var secretAddress = "https://evil.example/fcm/send/the-capability-of-a-browser";
        var good = Browser.Subscribe();
        using var host = _factory.WithPush();
        using var client = host.ClientFor(alice);

        var response = await client.PutAsJsonAsync("/api/push/subscriptions",
            new { endpoint = secretAddress, keys = new { p256dh = good.P256dh, auth = good.Auth } });

        Assert.DoesNotContain("the-capability-of-a-browser", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_body_without_keys_is_a_400_and_not_a_server_error()
    {
        var alice = await _factory.RegisterUserAsync("push");
        using var host = _factory.WithPush();
        using var client = host.ClientFor(alice);

        var response = await client.PutAsJsonAsync("/api/push/subscriptions", new { endpoint = Browser.Subscribe().Endpoint });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ##### Removing a browser #####

    [Fact]
    public async Task A_person_removes_their_own_browser()
    {
        var alice = await _factory.RegisterUserAsync("push");
        var browser = Browser.Subscribe();
        await _factory.AddSubscriptionAsync(alice.Id, browser);
        using var host = _factory.WithPush();
        using var client = host.ClientFor(alice);

        var response = await client.DeleteSubscriptionAsync(browser.Endpoint);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await _factory.SubscriptionsOfAsync(alice.Id));
    }

    // The answer is the same whether or not the browser is registered, and a person cannot remove the browser of someone else.
    [Fact]
    public async Task Removing_the_browser_of_another_person_changes_nothing_and_looks_like_removing_one_that_is_not_there()
    {
        var alice = await _factory.RegisterUserAsync("push");
        var mallory = await _factory.RegisterUserAsync("push");
        var browser = Browser.Subscribe();
        await _factory.AddSubscriptionAsync(alice.Id, browser);
        using var host = _factory.WithPush();
        using var malloryClient = host.ClientFor(mallory);

        var theirs = await malloryClient.DeleteSubscriptionAsync(browser.Endpoint);
        var missing = await malloryClient.DeleteSubscriptionAsync(Browser.Subscribe().Endpoint);

        Assert.Equal(HttpStatusCode.NoContent, theirs.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, missing.StatusCode);
        Assert.Single(await _factory.SubscriptionsOfAsync(alice.Id));
    }

    // A browser signing out has to be able to go whatever the list of allowed hosts says today.
    [Fact]
    public async Task A_browser_whose_address_is_no_longer_allowed_can_still_be_removed()
    {
        var alice = await _factory.RegisterUserAsync("push");
        var browser = Browser.Subscribe(host: "push.removed-from-the-list.example");
        await _factory.AddSubscriptionAsync(alice.Id, browser);
        using var host = _factory.WithPush();
        using var client = host.ClientFor(alice);

        var response = await client.DeleteSubscriptionAsync(browser.Endpoint);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await _factory.SubscriptionsOfAsync(alice.Id));
    }

    [Fact]
    public async Task Removing_works_when_push_has_been_switched_off()
    {
        var alice = await _factory.RegisterUserAsync("push");
        var browser = Browser.Subscribe();
        await _factory.AddSubscriptionAsync(alice.Id, browser);
        using var client = _factory.CreateClientFor(alice);

        var response = await client.DeleteSubscriptionAsync(browser.Endpoint);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await _factory.SubscriptionsOfAsync(alice.Id));
    }

    [Fact]
    public async Task Removing_without_an_address_is_a_400()
    {
        var alice = await _factory.RegisterUserAsync("push");
        using var host = _factory.WithPush();
        using var client = host.ClientFor(alice);

        var response = await client.DeleteSubscriptionAsync(string.Empty);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Removing_needs_a_token()
    {
        using var host = _factory.WithPush();
        using var anonymous = host.CreateClient();

        var response = await anonymous.DeleteSubscriptionAsync(Browser.Subscribe().Endpoint);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ##### The rate limit #####

    [Fact]
    public async Task Registering_and_removing_share_one_limit_per_person()
    {
        var alice = await _factory.RegisterUserAsync("push");
        var bob = await _factory.RegisterUserAsync("push");
        using var host = _factory.WithPush(settings: new Dictionary<string, string?>
        {
            ["RateLimiting:Push:PermitLimit"] = "2",
            ["RateLimiting:Push:WindowSeconds"] = "60",
        });
        using var aliceClient = host.ClientFor(alice);
        using var bobClient = host.ClientFor(bob);

        Assert.Equal(HttpStatusCode.NoContent, (await aliceClient.PutSubscriptionAsync(Browser.Subscribe())).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await aliceClient.DeleteSubscriptionAsync(Browser.Subscribe().Endpoint)).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await aliceClient.PutSubscriptionAsync(Browser.Subscribe())).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await bobClient.PutSubscriptionAsync(Browser.Subscribe())).StatusCode);
    }

    // ##### The table #####

    [Fact]
    public async Task Deleting_a_person_takes_their_browsers_with_them()
    {
        var alice = await _factory.RegisterUserAsync("push");
        await _factory.AddSubscriptionAsync(alice.Id, Browser.Subscribe());

        await _factory.ExecuteDbAsync(db =>
            db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM "AspNetUsers" WHERE "Id" = {alice.Id}"""));

        Assert.Empty(await _factory.SubscriptionsOfAsync(alice.Id));
    }

    [Fact]
    public async Task The_table_refuses_a_second_row_for_one_endpoint()
    {
        var alice = await _factory.RegisterUserAsync("push");
        var bob = await _factory.RegisterUserAsync("push");
        var browser = Browser.Subscribe();
        await _factory.AddSubscriptionAsync(alice.Id, browser);

        var failure = await Record.ExceptionAsync(() => _factory.AddSubscriptionAsync(bob.Id, browser));

        Assert.IsType<Microsoft.EntityFrameworkCore.DbUpdateException>(failure);
    }
}
