using System.Text.Json;
using ChatNET.API.Common.Persistence;
using ChatNET.API.Messaging.Hubs;
using ChatNET.API.Presence;
using ChatNET.API.Presence.Persistence;
using ChatNET.API.Tests.Infrastructure;
using ChatNET.API.Tests.Messaging;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace ChatNET.API.Tests.Presence;

// Presence as a client sees it: connect and disconnect real SignalR connections and watch who is told.
// Hosts here are separate application instances sharing one Redis and one database, which is how two
// servers behave. Unless a test says otherwise the timers are slow (see ChatApiFactory), so nothing
// happens by itself: tests run the refresh and the sweep when they want to.
[Collection(IntegrationTestCollection.Name)]
public class PresenceHubTests
{
    private readonly ChatApiFactory _factory;

    public PresenceHubTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // ##### Who is told #####

    [Fact]
    public async Task A_contact_is_told_when_someone_comes_online()
    {
        var (alice, bob) = await FriendsAsync();
        await using var bobClient = await HubClient.ConnectAsync(_factory, bob);

        await using var aliceClient = await HubClient.ConnectAsync(_factory, alice);

        var presence = await bobClient.WaitForPresenceAsync(alice.Id, isOnline: true);
        Assert.Equal(
            ["at", "isOnline", "lastSeenAt", "userId"],
            presence.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(JsonValueKind.Null, presence.GetProperty("lastSeenAt").ValueKind);
        Assert.InRange((DateTime.UtcNow - presence.GetProperty("at").GetDateTime()).TotalSeconds, -5, 10);
    }

    [Fact]
    public async Task Someone_who_shares_no_conversation_is_told_nothing()
    {
        var (alice, bob) = await FriendsAsync();
        var stranger = await _factory.RegisterUserAsync("stranger");
        await using var bobClient = await HubClient.ConnectAsync(_factory, bob);
        await using var strangerClient = await HubClient.ConnectAsync(_factory, stranger);

        await using var aliceClient = await HubClient.ConnectAsync(_factory, alice);

        await bobClient.WaitForPresenceAsync(alice.Id, isOnline: true);
        Assert.False(await strangerClient.ReceivedPresenceAsync(alice.Id));
    }

    [Fact]
    public async Task Every_member_of_a_group_is_a_contact()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var aliceApi = _factory.CreateClientFor(alice);
        await aliceApi.CreateGroupAsync("Team", bob.Id, carol.Id);
        await using var bobClient = await HubClient.ConnectAsync(_factory, bob);
        await using var carolClient = await HubClient.ConnectAsync(_factory, carol);

        await using var aliceClient = await HubClient.ConnectAsync(_factory, alice);

        await bobClient.WaitForPresenceAsync(alice.Id, isOnline: true);
        await carolClient.WaitForPresenceAsync(alice.Id, isOnline: true);
    }

    [Fact]
    public async Task A_member_who_left_the_conversation_is_not_told()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var aliceApi = _factory.CreateClientFor(alice);
        var group = (await aliceApi.CreateGroupAsync("Team", bob.Id, carol.Id)).IdOf();
        await _factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == group && m.UserId == carol.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LeftAt, DateTime.UtcNow)));
        await using var bobClient = await HubClient.ConnectAsync(_factory, bob);
        await using var carolClient = await HubClient.ConnectAsync(_factory, carol);

        await using var aliceClient = await HubClient.ConnectAsync(_factory, alice);

        await bobClient.WaitForPresenceAsync(alice.Id, isOnline: true);
        Assert.False(await carolClient.ReceivedPresenceAsync(alice.Id));
    }

    [Fact]
    public async Task The_person_themselves_is_not_told_about_their_own_presence()
    {
        var (alice, bob) = await FriendsAsync();
        await using var firstTab = await HubClient.ConnectAsync(_factory, alice);
        await using var bobClient = await HubClient.ConnectAsync(_factory, bob);

        await using var secondTab = await HubClient.ConnectAsync(_factory, alice);

        Assert.False(await firstTab.ReceivedPresenceAsync(alice.Id));
        Assert.False(await secondTab.ReceivedPresenceAsync(alice.Id));
    }

    [Fact]
    public async Task A_contact_on_another_server_instance_is_told()
    {
        var (alice, bob) = await FriendsAsync();
        using var otherInstance = _factory.WithWebHostBuilder(_ => { });
        await using var bobClient = await HubClient.ConnectAsync(otherInstance, bob);

        await using var aliceClient = await HubClient.ConnectAsync(_factory, alice);

        await bobClient.WaitForPresenceAsync(alice.Id, isOnline: true);
    }

    // ##### One change, one announcement #####

    [Fact]
    public async Task A_second_tab_is_not_a_change()
    {
        var (alice, bob) = await FriendsAsync();
        await using var bobClient = await HubClient.ConnectAsync(_factory, bob);
        await using var firstTab = await HubClient.ConnectAsync(_factory, alice);
        await bobClient.WaitForPresenceAsync(alice.Id, isOnline: true);

        await using var secondTab = await HubClient.ConnectAsync(_factory, alice);
        await Task.Delay(500);

        Assert.Single(bobClient.PresenceEvents, p => p.GetProperty("userId").GetGuid() == alice.Id);
    }

    [Fact]
    public async Task Closing_one_of_two_tabs_is_not_a_change_but_closing_the_last_one_is()
    {
        var (alice, bob) = await FriendsAsync();
        await using var bobClient = await HubClient.ConnectAsync(_factory, bob);
        await using var firstTab = await HubClient.ConnectAsync(_factory, alice);
        await using var secondTab = await HubClient.ConnectAsync(_factory, alice);
        await bobClient.WaitForPresenceAsync(alice.Id, isOnline: true);

        await firstTab.StopAsync();
        Assert.False(await bobClient.ReceivedPresenceOfflineAsync(alice.Id));

        await secondTab.StopAsync();
        var offline = await bobClient.WaitForPresenceAsync(alice.Id, isOnline: false);
        Assert.Equal(JsonValueKind.String, offline.GetProperty("lastSeenAt").ValueKind);
    }

    [Fact]
    public async Task Going_offline_stores_when_the_user_was_last_seen_and_the_events_are_in_order()
    {
        var (alice, bob) = await FriendsAsync();
        await using var bobClient = await HubClient.ConnectAsync(_factory, bob);
        await using var aliceClient = await HubClient.ConnectAsync(_factory, alice);
        var online = await bobClient.WaitForPresenceAsync(alice.Id, isOnline: true);

        await aliceClient.StopAsync();
        var offline = await bobClient.WaitForPresenceAsync(alice.Id, isOnline: false);

        var lastSeen = offline.GetProperty("lastSeenAt").GetDateTime();
        Assert.Equal(offline.GetProperty("at").GetDateTime(), lastSeen);
        Assert.True(online.GetProperty("at").GetDateTime() <= offline.GetProperty("at").GetDateTime());
        var stored = await _factory.QueryDbAsync(db => db.Users.Where(u => u.Id == alice.Id).Select(u => u.LastSeenAt).SingleAsync());
        Assert.Equal(lastSeen, stored);
    }

    [Fact]
    public async Task A_user_who_comes_back_is_announced_online_again()
    {
        var (alice, bob) = await FriendsAsync();
        await using var bobClient = await HubClient.ConnectAsync(_factory, bob);
        await using (var first = await HubClient.ConnectAsync(_factory, alice))
        {
            await bobClient.WaitForPresenceAsync(alice.Id, isOnline: true);
            await first.StopAsync();
            await bobClient.WaitForPresenceAsync(alice.Id, isOnline: false);
        }

        await using var second = await HubClient.ConnectAsync(_factory, alice);

        await WaitUntilAsync(() => bobClient.PresenceEvents.Count(p =>
            p.GetProperty("userId").GetGuid() == alice.Id && p.GetProperty("isOnline").GetBoolean()) == 2);
    }

    // ##### A page refresh #####

    // Closing a connection and opening another a moment later is what a refresh looks like. With a
    // short wait before "offline", nobody sees the user go offline and come back.
    [Fact]
    public async Task A_user_who_reconnects_within_the_wait_is_never_announced_offline()
    {
        var (alice, bob) = await FriendsAsync();
        using var host = HostWith(o => o.OfflineGrace = TimeSpan.FromSeconds(1));
        await using var bobClient = await HubClient.ConnectAsync(host, bob);
        var oldTab = await HubClient.ConnectAsync(host, alice);
        await bobClient.WaitForPresenceAsync(alice.Id, isOnline: true);

        await oldTab.StopAsync();
        await using var newTab = await HubClient.ConnectAsync(host, alice);
        await Task.Delay(1600);

        var about = bobClient.PresenceEvents.Where(p => p.GetProperty("userId").GetGuid() == alice.Id).ToList();
        Assert.Single(about);
        Assert.True(about[0].GetProperty("isOnline").GetBoolean());
        await oldTab.DisposeAsync();
    }

    // Found by closing and reopening a tab twice in a row in a browser: each close starts a timer, and
    // the first one used to take the user offline while the second close's wait was still running.
    [Fact]
    public async Task The_wait_is_counted_from_the_last_close_when_connections_open_and_close_in_between()
    {
        var (alice, bob) = await FriendsAsync();
        using var host = HostWith(o => o.OfflineGrace = TimeSpan.FromSeconds(1));
        await using var bobClient = await HubClient.ConnectAsync(host, bob);
        var firstTab = await HubClient.ConnectAsync(host, alice);
        await bobClient.WaitForPresenceAsync(alice.Id, isOnline: true);

        await firstTab.StopAsync();
        var secondTab = await HubClient.ConnectAsync(host, alice);
        await Task.Delay(400);
        var sinceLastClose = System.Diagnostics.Stopwatch.StartNew();
        await secondTab.StopAsync();

        // 750 ms on is past the end of the first close's wait and inside the second's.
        await Task.Delay(750);
        var earlyEnough = OfflineEventsAbout(bobClient, alice).Count;
        await bobClient.WaitForPresenceAsync(alice.Id, isOnline: false, TimeSpan.FromSeconds(4));
        var announcedAfter = sinceLastClose.Elapsed;
        await Task.Delay(600);

        Assert.Equal(0, earlyEnough);
        Assert.True(announcedAfter >= TimeSpan.FromMilliseconds(900), $"Offline was announced {announcedAfter} after the last close.");
        Assert.Single(OfflineEventsAbout(bobClient, alice));
        await firstTab.DisposeAsync();
        await secondTab.DisposeAsync();
    }

    // The store can answer "a wait is still running" to a timer that woke up (a later close restarted
    // it, or the timer woke a hair early). The timer must sleep for what is left and try again, not
    // give up: nothing else would take the user offline until their entries expire.
    [Fact]
    public async Task A_timer_told_that_a_wait_is_still_running_sleeps_for_the_rest_and_finishes()
    {
        var (alice, bob) = await FriendsAsync();
        var store = new WaitOnceStore(_factory.Services.GetRequiredService<IPresenceStore>());
        using var host = HostWith(o => o.OfflineGrace = TimeSpan.FromMilliseconds(200), services =>
        {
            services.RemoveAll<IPresenceStore>();
            services.AddSingleton<IPresenceStore>(store);
        });
        await using var bobClient = await HubClient.ConnectAsync(host, bob);
        var aliceClient = await HubClient.ConnectAsync(host, alice);
        await bobClient.WaitForPresenceAsync(alice.Id, isOnline: true);

        await aliceClient.StopAsync();
        await bobClient.WaitForPresenceAsync(alice.Id, isOnline: false, TimeSpan.FromSeconds(4));

        var calls = store.FinishCalls;
        Assert.Equal(2, calls.Count);
        Assert.True(calls[1] - calls[0] >= WaitOnceStore.Wait - TimeSpan.FromMilliseconds(50),
            $"The timer asked again after {calls[1] - calls[0]}, not after the {WaitOnceStore.Wait} it was told to wait.");
        await aliceClient.DisposeAsync();
    }

    [Fact]
    public async Task Offline_is_announced_after_the_wait_and_not_before()
    {
        var (alice, bob) = await FriendsAsync();
        using var host = HostWith(o => o.OfflineGrace = TimeSpan.FromSeconds(1));
        await using var bobClient = await HubClient.ConnectAsync(host, bob);
        var aliceClient = await HubClient.ConnectAsync(host, alice);
        await bobClient.WaitForPresenceAsync(alice.Id, isOnline: true);

        await aliceClient.StopAsync();
        await Task.Delay(500);
        var earlyEnough = bobClient.PresenceEvents.Any(p =>
            p.GetProperty("userId").GetGuid() == alice.Id && !p.GetProperty("isOnline").GetBoolean());
        var offline = await bobClient.WaitForPresenceAsync(alice.Id, isOnline: false, TimeSpan.FromSeconds(4));

        Assert.False(earlyEnough, "Offline was announced before the wait was over.");
        Assert.Equal(JsonValueKind.String, offline.GetProperty("lastSeenAt").ValueKind);
        await aliceClient.DisposeAsync();
    }

    // ##### Entries that nobody renews #####

    [Fact]
    public async Task Renewal_keeps_a_connected_user_online_for_much_longer_than_an_entry_lasts()
    {
        var (alice, bob) = await FriendsAsync();
        using var host = HostWith(o =>
        {
            o.Ttl = TimeSpan.FromSeconds(1);
            o.RefreshInterval = TimeSpan.FromMilliseconds(250);
        });
        await using var bobClient = await HubClient.ConnectAsync(host, bob);
        await using var aliceClient = await HubClient.ConnectAsync(host, alice);
        await bobClient.WaitForPresenceAsync(alice.Id, isOnline: true);

        await Task.Delay(2500);

        Assert.False(await bobClient.ReceivedPresenceOfflineAsync(alice.Id));
        var snapshot = await host.Services.GetRequiredService<IPresenceStore>().GetSnapshotAsync([alice.Id], default);
        Assert.Contains(alice.Id, snapshot.Online);
        // Renewing is not a change: about ten refreshes ran and the user was announced online once.
        Assert.Single(bobClient.PresenceEvents, p => p.GetProperty("userId").GetGuid() == alice.Id);
    }

    // A connection that has closed is forgotten, so no later refresh renews it and brings the user back.
    [Fact]
    public async Task A_closed_connection_is_not_renewed_by_the_next_refresh()
    {
        var (alice, bob) = await FriendsAsync();
        await using var bobClient = await HubClient.ConnectAsync(_factory, bob);
        var aliceClient = await HubClient.ConnectAsync(_factory, alice);
        await bobClient.WaitForPresenceAsync(alice.Id, isOnline: true);
        await aliceClient.StopAsync();
        await bobClient.WaitForPresenceAsync(alice.Id, isOnline: false);

        await _factory.Services.GetRequiredService<PresenceRefreshService>().RunOnceAsync(default);
        await Task.Delay(500);

        Assert.Single(bobClient.PresenceEvents, p =>
            p.GetProperty("userId").GetGuid() == alice.Id && p.GetProperty("isOnline").GetBoolean());
        var snapshot = await _factory.Services.GetRequiredService<IPresenceStore>().GetSnapshotAsync([alice.Id], default);
        Assert.DoesNotContain(alice.Id, snapshot.Online);
        await aliceClient.DisposeAsync();
    }

    // A server that dies leaves its entries behind. Two surviving servers both find them and both try
    // to take the user offline; exactly one does, so exactly one announcement is made.
    [Fact]
    public async Task An_entry_left_behind_by_a_dead_server_is_swept_and_announced_exactly_once()
    {
        var (ghost, bob) = await FriendsAsync();
        // Both servers also run their own sweep loops, so besides the two sweeps the test starts there are
        // others racing for the same user: exactly one of all of them may announce it.
        void ShortLived(PresenceOptions o)
        {
            o.Ttl = TimeSpan.FromSeconds(1);
            o.RefreshInterval = TimeSpan.FromMilliseconds(500);
        }

        using var serverA = HostWith(ShortLived);
        using var serverB = HostWith(ShortLived);
        await using var bobClient = await HubClient.ConnectAsync(serverA, bob);
        var store = serverA.Services.GetRequiredService<IPresenceStore>();
        // The dead server's connection: an entry in Redis that no running server holds.
        var touched = await store.TouchAsync(ghost.Id, "connection-of-a-dead-server", default);
        Assert.True(touched.CameOnline);
        await Task.Delay(1400);

        await Task.WhenAll(
            serverA.Services.GetRequiredService<PresenceRefreshService>().RunOnceAsync(default),
            serverB.Services.GetRequiredService<PresenceRefreshService>().RunOnceAsync(default));

        var offline = await bobClient.WaitForPresenceAsync(ghost.Id, isOnline: false);
        await Task.Delay(500);
        Assert.Single(bobClient.PresenceEvents, p =>
            p.GetProperty("userId").GetGuid() == ghost.Id && !p.GetProperty("isOnline").GetBoolean());
        // Last seen is when the entry was last renewed, not when it was swept.
        var lastSeen = offline.GetProperty("lastSeenAt").GetDateTime();
        Assert.InRange((lastSeen - touched.At).TotalMilliseconds, -50, 50);
    }

    // Racing real servers rarely lines up two sweepers on the same user, so this one is deterministic: the
    // store says someone else already took the user offline, and the sweeper must say nothing.
    [Fact]
    public async Task A_sweeper_that_loses_the_race_to_take_a_user_offline_announces_nothing()
    {
        var (ghost, bob) = await FriendsAsync();
        using var host = HostWith(_ => { }, services =>
        {
            services.RemoveAll<IPresenceStore>();
            services.AddSingleton<IPresenceStore>(new LosingSweepStore(ghost.Id));
        });
        await using var bobClient = await HubClient.ConnectAsync(_factory, bob);

        await host.Services.GetRequiredService<PresenceRefreshService>().RunOnceAsync(default);

        Assert.False(await bobClient.ReceivedPresenceAsync(ghost.Id));
    }

    [Fact]
    public async Task A_server_brings_its_connected_users_back_when_redis_lost_their_entries()
    {
        var (alice, bob) = await FriendsAsync();
        await using var bobClient = await HubClient.ConnectAsync(_factory, bob);
        await using var aliceClient = await HubClient.ConnectAsync(_factory, alice);
        await bobClient.WaitForPresenceAsync(alice.Id, isOnline: true);
        var db = await _factory.Services.GetRequiredService<RedisConnection>().GetDatabaseAsync();
        await db.KeyDeleteAsync($"chatnet:presence:user:{alice.Id}");
        await db.SortedSetRemoveAsync("chatnet:presence:expiring", alice.Id.ToString());

        await _factory.Services.GetRequiredService<PresenceRefreshService>().RunOnceAsync(default);

        await WaitUntilAsync(() => bobClient.PresenceEvents.Count(p =>
            p.GetProperty("userId").GetGuid() == alice.Id && p.GetProperty("isOnline").GetBoolean()) == 2);
    }

    // ##### When Redis or SignalR fail #####

    // Presence is a nicety. A Redis that does not answer must not stop anyone connecting or chatting.
    [Fact]
    public async Task A_failing_presence_store_does_not_stop_a_connection_and_is_logged_with_ids_only()
    {
        var capture = new CapturingLoggerProvider();
        var (alice, bob) = await FriendsAsync();
        var flaky = new FlakyPresenceStore(_factory.Services.GetRequiredService<IPresenceStore>());
        using var host = HostWith(_ => { }, services =>
        {
            services.RemoveAll<IPresenceStore>();
            services.AddSingleton<IPresenceStore>(flaky);
            services.AddLogging(logging => logging.AddProvider(capture));
        });

        await using var aliceClient = await HubClient.ConnectAsync(host, alice);
        using var aliceApi = host.CreateClient();
        aliceApi.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", alice.Token);
        using var directApi = _factory.CreateClientFor(alice);
        var conversation = (await directApi.CreateDirectAsync(bob.Id)).IdOf();
        var send = await aliceApi.PostMessageAsync(conversation, "still works");

        Assert.Equal(System.Net.HttpStatusCode.Created, send.StatusCode);
        Assert.Equal(Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected, aliceClient.State);
        var warning = Assert.Single(capture.Entries, e => e.StartsWith("[Warning]") && e.Contains(alice.Id.ToString()));
        Assert.DoesNotContain("still works", warning);
    }

    [Fact]
    public async Task The_next_refresh_corrects_what_a_failing_store_missed()
    {
        var (alice, bob) = await FriendsAsync();
        var flaky = new FlakyPresenceStore(_factory.Services.GetRequiredService<IPresenceStore>());
        using var host = HostWith(_ => { }, services =>
        {
            services.RemoveAll<IPresenceStore>();
            services.AddSingleton<IPresenceStore>(flaky);
        });
        await using var bobClient = await HubClient.ConnectAsync(_factory, bob);
        await using var aliceClient = await HubClient.ConnectAsync(host, alice);
        Assert.False(await bobClient.ReceivedPresenceAsync(alice.Id));

        flaky.Healthy();
        await host.Services.GetRequiredService<PresenceRefreshService>().RunOnceAsync(default);

        await bobClient.WaitForPresenceAsync(alice.Id, isOnline: true);
    }

    [Fact]
    public async Task A_failing_push_does_not_stop_a_connection_and_is_logged_with_ids_only()
    {
        var capture = new CapturingLoggerProvider();
        var (alice, bob) = await FriendsAsync();
        using var host = HostWith(_ => { }, services =>
        {
            services.RemoveAll<IHubContext<ChatHub, IChatClient>>();
            services.AddSingleton<IHubContext<ChatHub, IChatClient>>(new ThrowingHubContext());
            services.AddLogging(logging => logging.AddProvider(capture));
        });

        // The hub itself needs real groups, which the replaced context cannot give; the service is what
        // is under test here, so it is driven directly with a connection id.
        var presence = host.Services.GetRequiredService<PresenceService>();
        await presence.ConnectedAsync(alice.Id, "connection-1");

        Assert.Contains(capture.Entries, e => e.StartsWith("[Warning]") && e.Contains(alice.Id.ToString()));
        Assert.DoesNotContain(capture.Entries, e => e.Contains(alice.Email));
        await presence.DisconnectedAsync(alice.Id, "connection-1");
    }

    // ##### Last seen #####

    [Fact]
    public async Task A_last_seen_time_never_moves_back()
    {
        var user = await _factory.RegisterUserAsync("alice");
        using var scope = _factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPresenceRepository>();
        var later = DateTime.UtcNow.AddMinutes(-1);

        await repository.SetLastSeenAsync(user.Id, later, default);
        await repository.SetLastSeenAsync(user.Id, later.AddMinutes(-10), default);

        var stored = await repository.GetLastSeenAsync([user.Id], default);
        Assert.Equal(later, stored[user.Id]!.Value, TimeSpan.FromMilliseconds(1));
    }

    // ##### Helpers #####

    private async Task<(TestUser Alice, TestUser Bob)> FriendsAsync()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);
        await client.CreateDirectAsync(bob.Id);
        return (alice, bob);
    }

    private WebApplicationFactory<Program> HostWith(
        Action<PresenceOptions> options, Action<IServiceCollection>? services = null) =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(s =>
        {
            s.PostConfigure(options);
            services?.Invoke(s);
        }));

    private static List<JsonElement> OfflineEventsAbout(HubClient client, TestUser user) =>
        client.PresenceEvents
            .Where(p => p.GetProperty("userId").GetGuid() == user.Id && !p.GetProperty("isOnline").GetBoolean())
            .ToList();

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The expected presence events did not arrive.");

            await Task.Delay(25);
        }
    }

    // A store in which one user's entries have expired but another server has already taken them offline:
    // every sweep finds them, and every attempt to take them offline loses.
    private sealed class LosingSweepStore : IPresenceStore
    {
        private readonly Guid _user;

        public LosingSweepStore(Guid user)
        {
            _user = user;
        }

        public Task<ExpiredUsers> GetExpiredAsync(int max, CancellationToken cancellationToken) =>
            Task.FromResult(new ExpiredUsers(DateTime.UtcNow, [new ExpiredUser(_user, DateTime.UtcNow.AddMinutes(-2))]));

        public Task<SettleResult> FinishAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult(new SettleResult(SettleOutcome.AlreadyOffline, DateTime.UtcNow));

        public Task<SettleResult> CloseAsync(Guid userId, string connectionId, TimeSpan grace, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TouchResult> TouchAsync(Guid userId, string connectionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TouchResult>> TouchManyAsync(
            IReadOnlyList<(Guid UserId, string ConnectionId)> connections, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PresenceSnapshot> GetSnapshotAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    // A store that answers the first request to finish a user with "a wait is still running, for another
    // 300 ms" and notes when it was asked; everything else is the real store.
    private sealed class WaitOnceStore : IPresenceStore
    {
        public static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(300);

        private readonly IPresenceStore _real;
        private readonly List<DateTime> _finishCalls = [];

        public WaitOnceStore(IPresenceStore real)
        {
            _real = real;
        }

        public IReadOnlyList<DateTime> FinishCalls
        {
            get
            {
                lock (_finishCalls)
                    return _finishCalls.ToList();
            }
        }

        public Task<SettleResult> FinishAsync(Guid userId, CancellationToken cancellationToken)
        {
            bool first;
            lock (_finishCalls)
            {
                _finishCalls.Add(DateTime.UtcNow);
                first = _finishCalls.Count == 1;
            }

            return first
                ? Task.FromResult(new SettleResult(SettleOutcome.NoConnectionsLeft, DateTime.UtcNow, Wait))
                : _real.FinishAsync(userId, cancellationToken);
        }

        public Task<SettleResult> CloseAsync(Guid userId, string connectionId, TimeSpan grace, CancellationToken cancellationToken)
            => _real.CloseAsync(userId, connectionId, grace, cancellationToken);

        public Task<TouchResult> TouchAsync(Guid userId, string connectionId, CancellationToken cancellationToken)
            => _real.TouchAsync(userId, connectionId, cancellationToken);

        public Task<IReadOnlyList<TouchResult>> TouchManyAsync(
            IReadOnlyList<(Guid UserId, string ConnectionId)> connections, CancellationToken cancellationToken)
            => _real.TouchManyAsync(connections, cancellationToken);

        public Task<ExpiredUsers> GetExpiredAsync(int max, CancellationToken cancellationToken)
            => _real.GetExpiredAsync(max, cancellationToken);

        public Task<PresenceSnapshot> GetSnapshotAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
            => _real.GetSnapshotAsync(userIds, cancellationToken);
    }

    // A store that fails like an unreachable Redis until told it is healthy again, then behaves like the
    // real one.
    private sealed class FlakyPresenceStore : IPresenceStore
    {
        private readonly IPresenceStore _real;
        private volatile bool _healthy;

        public FlakyPresenceStore(IPresenceStore real)
        {
            _real = real;
        }

        public void Healthy() => _healthy = true;

        private IPresenceStore Real => _healthy
            ? _real
            : throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Simulated: Redis is down.");

        public Task<TouchResult> TouchAsync(Guid userId, string connectionId, CancellationToken cancellationToken)
            => Real.TouchAsync(userId, connectionId, cancellationToken);

        public Task<IReadOnlyList<TouchResult>> TouchManyAsync(
            IReadOnlyList<(Guid UserId, string ConnectionId)> connections, CancellationToken cancellationToken)
            => Real.TouchManyAsync(connections, cancellationToken);

        public Task<SettleResult> CloseAsync(Guid userId, string connectionId, TimeSpan grace, CancellationToken cancellationToken)
            => Real.CloseAsync(userId, connectionId, grace, cancellationToken);

        public Task<SettleResult> FinishAsync(Guid userId, CancellationToken cancellationToken)
            => Real.FinishAsync(userId, cancellationToken);

        public Task<ExpiredUsers> GetExpiredAsync(int max, CancellationToken cancellationToken)
            => Real.GetExpiredAsync(max, cancellationToken);

        public Task<PresenceSnapshot> GetSnapshotAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
            => Real.GetSnapshotAsync(userIds, cancellationToken);
    }
}
