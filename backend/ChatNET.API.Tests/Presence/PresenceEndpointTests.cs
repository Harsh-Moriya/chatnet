using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatNET.API.Presence;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatNET.API.Tests.Presence;

// GET /api/presence is how a client learns who is online when it starts and after a reconnect, since the
// pushes sent while it was away are gone. It has to agree with the pushes: the same shape, the same
// idea of who is online.
[Collection(IntegrationTestCollection.Name)]
public class PresenceEndpointTests
{
    private readonly ChatApiFactory _factory;

    public PresenceEndpointTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Presence_requires_authentication()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/presence");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(401, (await response.ReadProblemAsync()).Status);
    }

    [Fact]
    public async Task A_user_with_no_conversations_has_no_contacts()
    {
        var loner = await _factory.RegisterUserAsync("loner");
        using var client = _factory.CreateClientFor(loner);

        Assert.Empty(await client.GetPresenceAsync());
    }

    [Fact]
    public async Task It_lists_exactly_the_people_who_share_an_active_conversation_and_never_the_caller()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        var dave = await _factory.RegisterUserAsync("dave");
        var stranger = await _factory.RegisterUserAsync("stranger");
        using var aliceClient = _factory.CreateClientFor(alice);
        var withBob = (await aliceClient.CreateDirectAsync(bob.Id)).IdOf();
        var group = (await aliceClient.CreateGroupAsync("Team", carol.Id, dave.Id)).IdOf();
        await _factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == group && m.UserId == dave.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LeftAt, DateTime.UtcNow)));

        var presence = await aliceClient.GetPresenceAsync();

        Assert.Equivalent(new[] { bob.Id, carol.Id }, presence.Select(p => p.GetProperty("userId").GetGuid()));
        Assert.DoesNotContain(presence, p => p.GetProperty("userId").GetGuid() == stranger.Id);
        Assert.DoesNotContain(presence, p => p.GetProperty("userId").GetGuid() == alice.Id);
        Assert.NotEqual(Guid.Empty, withBob);
    }

    [Fact]
    public async Task A_contact_in_two_conversations_is_listed_once()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceClient = _factory.CreateClientFor(alice);
        await aliceClient.CreateDirectAsync(bob.Id);
        await aliceClient.CreateGroupAsync("Team", bob.Id);

        var presence = await aliceClient.GetPresenceAsync();

        Assert.Single(presence);
    }

    [Fact]
    public async Task Each_entry_has_exactly_the_documented_fields_and_every_entry_shares_one_time()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var aliceClient = _factory.CreateClientFor(alice);
        await aliceClient.CreateGroupAsync("Team", bob.Id, carol.Id);

        var presence = await aliceClient.GetPresenceAsync();

        Assert.All(presence, p => Assert.Equal(
            ["at", "isOnline", "lastSeenAt", "userId"], p.EnumerateObject().Select(f => f.Name).Order()));
        Assert.Single(presence.Select(p => p.GetProperty("at").GetDateTime()).Distinct());
        Assert.EndsWith("Z", presence[0].GetProperty("at").GetString());
        Assert.InRange((DateTime.UtcNow - presence[0].GetProperty("at").GetDateTime()).TotalSeconds, -5, 10);
    }

    [Fact]
    public async Task Online_contacts_are_marked_online_and_the_rest_are_not()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var aliceClient = _factory.CreateClientFor(alice);
        await aliceClient.CreateGroupAsync("Team", bob.Id, carol.Id);
        await using var bobTab = await HubClient.ConnectAsync(_factory, bob);

        var presence = await aliceClient.GetPresenceAsync();

        Assert.True(presence.Single(p => p.GetProperty("userId").GetGuid() == bob.Id).GetProperty("isOnline").GetBoolean());
        Assert.False(presence.Single(p => p.GetProperty("userId").GetGuid() == carol.Id).GetProperty("isOnline").GetBoolean());
    }

    [Fact]
    public async Task A_contact_who_went_offline_shows_when_they_were_last_seen_and_one_who_never_was_online_shows_none()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var aliceClient = _factory.CreateClientFor(alice);
        await aliceClient.CreateGroupAsync("Team", bob.Id, carol.Id);
        await using var aliceHub = await HubClient.ConnectAsync(_factory, alice);
        var bobHub = await HubClient.ConnectAsync(_factory, bob);
        await aliceHub.WaitForPresenceAsync(bob.Id, isOnline: true);
        await bobHub.StopAsync();
        var offline = await aliceHub.WaitForPresenceAsync(bob.Id, isOnline: false);

        var presence = await aliceClient.GetPresenceAsync();

        var forBob = presence.Single(p => p.GetProperty("userId").GetGuid() == bob.Id);
        Assert.False(forBob.GetProperty("isOnline").GetBoolean());
        Assert.Equal(offline.GetProperty("lastSeenAt").GetDateTime(), forBob.GetProperty("lastSeenAt").GetDateTime());
        var forCarol = presence.Single(p => p.GetProperty("userId").GetGuid() == carol.Id);
        Assert.Equal(JsonValueKind.Null, forCarol.GetProperty("lastSeenAt").ValueKind);
        await bobHub.DisposeAsync();
    }

    // A contact who came back online keeps the time they were last seen before: it only changes when
    // they go offline again.
    [Fact]
    public async Task An_online_contact_keeps_their_previous_last_seen_time()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceClient = _factory.CreateClientFor(alice);
        await aliceClient.CreateDirectAsync(bob.Id);
        await using var aliceHub = await HubClient.ConnectAsync(_factory, alice);
        var first = await HubClient.ConnectAsync(_factory, bob);
        await aliceHub.WaitForPresenceAsync(bob.Id, isOnline: true);
        await first.StopAsync();
        var offline = await aliceHub.WaitForPresenceAsync(bob.Id, isOnline: false);
        await using var second = await HubClient.ConnectAsync(_factory, bob);

        var presence = await aliceClient.GetPresenceAsync();

        var forBob = Assert.Single(presence);
        Assert.True(forBob.GetProperty("isOnline").GetBoolean());
        Assert.Equal(offline.GetProperty("lastSeenAt").GetDateTime(), forBob.GetProperty("lastSeenAt").GetDateTime());
        await first.DisposeAsync();
    }

    // During the short wait before "offline" nobody has been told the user left, so a snapshot taken then
    // must still say online: otherwise the person who loaded it would show them offline, and the user's
    // return would not change that, because no event is sent for it.
    [Fact]
    public async Task A_contact_in_the_wait_before_offline_is_still_listed_as_online()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceClient = _factory.CreateClientFor(alice);
        await aliceClient.CreateDirectAsync(bob.Id);
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.PostConfigure<PresenceOptions>(o => o.OfflineGrace = TimeSpan.FromSeconds(3))));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", alice.Token);
        var bobHub = await HubClient.ConnectAsync(host, bob);
        await bobHub.StopAsync();
        await Task.Delay(400);

        var presence = await client.GetPresenceAsync();

        Assert.True(Assert.Single(presence).GetProperty("isOnline").GetBoolean());
        await bobHub.DisposeAsync();
    }

    [Fact]
    public async Task The_snapshot_is_never_older_than_an_event_that_was_already_sent()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceClient = _factory.CreateClientFor(alice);
        await aliceClient.CreateDirectAsync(bob.Id);
        await using var aliceHub = await HubClient.ConnectAsync(_factory, alice);
        await using var bobHub = await HubClient.ConnectAsync(_factory, bob);
        var online = await aliceHub.WaitForPresenceAsync(bob.Id, isOnline: true);

        var presence = await aliceClient.GetPresenceAsync();

        Assert.True(presence[0].GetProperty("at").GetDateTime() >= online.GetProperty("at").GetDateTime());
    }
}

internal static class PresenceApi
{
    public static async Task<List<JsonElement>> GetPresenceAsync(this HttpClient client)
    {
        var response = await client.GetAsync("/api/presence");
        response.EnsureSuccessStatusCode();
        var list = await response.Content.ReadFromJsonAsync<JsonElement>();
        return list.EnumerateArray().ToList();
    }
}
