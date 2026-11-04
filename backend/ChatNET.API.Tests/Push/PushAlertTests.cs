using System.Net;
using System.Text.Json;
using ChatNET.API.Outbox;
using ChatNET.API.Presence;
using ChatNET.API.Push;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Microsoft.Extensions.Logging;

namespace ChatNET.API.Tests.Push;

// The whole path of an alert: a request saves the rows that ask for it, the relay takes them, finds who is away and which browsers
// they have, and posts to the push services (a fake one here, which records what it was sent and answers as a test says). The
// browsers are real keys, so what the fake received is read the way a browser reads it.
[Collection(IntegrationTestCollection.Name)]
public class PushAlertTests : IAsyncLifetime
{
    private readonly ChatApiFactory _factory;

    public PushAlertTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // Rows other tests left waiting would be taken by the passes these tests run.
    public async Task InitializeAsync()
    {
        await _factory.RemoveWaitingOutboxRowsAsync();
        await _factory.RemoveSubscriptionsAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.RemoveWaitingOutboxRowsAsync();
        await _factory.RemoveSubscriptionsAsync();
    }

    // A host with push on, whose pushes go to the fake service, with the clock the test moves.
    private sealed class Rig : IDisposable
    {
        public required WebApplicationFactory<Program> Host { get; init; }
        public required FakePushService Service { get; init; }
        public required CapturingLoggerProvider Log { get; init; }
        public required ManualTimeProvider Clock { get; init; }

        public HttpClient ClientFor(TestUser user) => Host.ClientFor(user);

        public Task RunRelayAsync() => Host.Services.GetRequiredService<OutboxRelayService>().RunOnceAsync(CancellationToken.None);

        public void Dispose() => Host.Dispose();
    }

    private Rig NewRig(IDictionary<string, string?>? settings = null, Action<IServiceCollection>? configureServices = null)
    {
        var service = new FakePushService();
        var log = new CapturingLoggerProvider();
        var clock = new ManualTimeProvider();

        var host = _factory.WithPush(
            builder => builder
                .ConfigureTestServices(services =>
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(clock);
                    services.AddHttpClient<IPushSender, WebPushSender>().ConfigurePrimaryHttpMessageHandler(() => service);
                    configureServices?.Invoke(services);
                })
                .ConfigureLogging(logging => logging.AddProvider(log)),
            settings);

        return new Rig { Host = host, Service = service, Log = log, Clock = clock };
    }

    private async Task<(TestUser Alice, TestUser Bob, TestUser Carol, Guid Team)> TeamAsync()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");

        // Made on the shared host, which has no push keys, so no invitation is waiting to be alerted.
        using var client = _factory.CreateClientFor(alice);
        return (alice, bob, carol, (await client.CreateGroupAsync("Team", bob.Id, carol.Id)).IdOf());
    }

    private async Task<Browser> SubscribeAsync(TestUser user)
    {
        var browser = Browser.Subscribe();
        await _factory.AddSubscriptionAsync(user.Id, browser);
        return browser;
    }

    // ##### The rows #####

    [Fact]
    public async Task A_message_saves_a_row_for_the_relay_beside_the_one_the_request_pushes()
    {
        var (alice, _, _, team) = await TeamAsync();
        using var rig = NewRig();
        using var client = rig.ClientFor(alice);

        var message = await client.SendMessageAsync(team, "hello team");

        var rows = await _factory.OutboxRowsAboutAsync("messageId", message.MessageIdOf());
        Assert.Equal(["MessageAlertRequested", "MessageCreated"], rows.Select(r => r.Kind).Order());

        // The request pushed its own row; the alert row is not its to push, and is due at once.
        var alert = rows.Single(r => r.Kind == "MessageAlertRequested");
        Assert.Null(alert.DispatchedAt);
        Assert.Equal(alert.CreatedAt, alert.NextAttemptAt);
        Assert.NotNull(rows.Single(r => r.Kind == "MessageCreated").DispatchedAt);
    }

    [Fact]
    public async Task A_server_without_push_keys_saves_no_alert_rows()
    {
        var (alice, _, _, team) = await TeamAsync();
        using var client = _factory.CreateClientFor(alice);

        var message = await client.SendMessageAsync(team, "hello team");

        var rows = await _factory.OutboxRowsAboutAsync("messageId", message.MessageIdOf());
        Assert.Equal(["MessageCreated"], rows.Select(r => r.Kind));
    }

    [Fact]
    public async Task A_mention_saves_a_row_for_the_person_it_names()
    {
        var (alice, bob, _, team) = await TeamAsync();
        using var rig = NewRig();
        using var client = rig.ClientFor(alice);

        var message = await client.SendMessageAsync(team, $"hello @{bob.UserName}");

        var notification = await _factory.QueryDbAsync(db =>
            db.Notifications.AsNoTracking().SingleAsync(n => n.MessageId == message.MessageIdOf()));
        var row = Assert.Single(await _factory.OutboxRowsAboutAsync("notificationId", notification.Id), r => r.Kind == "NotificationAlertRequested");
        Assert.Equal(bob.Id, JsonDocument.Parse(row.Payload).RootElement.GetProperty("userId").GetGuid());
    }

    // ##### Who is alerted for a message #####

    [Fact]
    public async Task A_member_who_is_away_is_alerted_with_the_words_of_the_message_and_nobody_else_is()
    {
        var (alice, bob, carol, team) = await TeamAsync();
        var bobsBrowser = await SubscribeAsync(bob);
        await SubscribeAsync(alice);
        using var rig = NewRig();
        using var client = rig.ClientFor(alice);
        var message = await client.SendMessageAsync(team, "hello team");

        await rig.RunRelayAsync();

        // Carol has no browser registered; Alice sent it.
        var push = Assert.Single(rig.Service.Received);
        Assert.Equal(bobsBrowser.Endpoint, push.Uri.ToString());
        var alert = push.AlertFor(bobsBrowser);
        Assert.Equal($"{alice.DisplayName} in Team", alert.GetProperty("title").GetString());
        Assert.Equal("hello team", alert.GetProperty("body").GetString());
        Assert.Equal("message", alert.GetProperty("type").GetString());
        Assert.Equal($"conversation-{team}", alert.GetProperty("tag").GetString());
        Assert.Equal(team, alert.GetProperty("conversationId").GetGuid());
        Assert.Equal(message.MessageIdOf(), alert.GetProperty("messageId").GetGuid());
        _ = carol;
    }

    [Fact]
    public async Task Every_browser_of_a_person_who_is_away_is_alerted()
    {
        var (alice, bob, _, team) = await TeamAsync();
        var laptop = await SubscribeAsync(bob);
        var phone = await SubscribeAsync(bob);
        using var rig = NewRig();
        using var client = rig.ClientFor(alice);
        await client.SendMessageAsync(team, "hello team");

        await rig.RunRelayAsync();

        Assert.Single(rig.Service.ReceivedBy(laptop));
        Assert.Single(rig.Service.ReceivedBy(phone));
    }

    [Fact]
    public async Task Everybody_who_is_away_is_alerted_once_each()
    {
        var (alice, bob, carol, team) = await TeamAsync();
        var bobsBrowser = await SubscribeAsync(bob);
        var carolsBrowser = await SubscribeAsync(carol);
        using var rig = NewRig();
        using var client = rig.ClientFor(alice);
        await client.SendMessageAsync(team, "hello team");

        await rig.RunRelayAsync();

        Assert.Equal(2, rig.Service.Received.Count);
        Assert.Single(rig.Service.ReceivedBy(bobsBrowser));
        Assert.Single(rig.Service.ReceivedBy(carolsBrowser));
    }

    [Fact]
    public async Task A_direct_message_is_titled_with_the_sender()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        var bobsBrowser = await SubscribeAsync(bob);
        using var rig = NewRig();
        using var client = rig.ClientFor(alice);
        await client.SendMessageAsync(conversation, "are you there?");

        await rig.RunRelayAsync();

        var alert = Assert.Single(rig.Service.Received).AlertFor(bobsBrowser);
        Assert.Equal(alice.DisplayName, alert.GetProperty("title").GetString());
        Assert.Equal("are you there?", alert.GetProperty("body").GetString());
    }

    [Fact]
    public async Task A_message_of_only_files_says_how_many()
    {
        var (alice, bob, _, team) = await TeamAsync();
        var bobsBrowser = await SubscribeAsync(bob);
        using var rig = NewRig();
        using var client = rig.ClientFor(alice);
        var first = await client.UploadFileAsync(team, [1, 2, 3], fileName: "one.png");
        var second = await client.UploadFileAsync(team, [4, 5, 6], fileName: "two.png");
        await client.SendMessageWithFilesAsync(team, null, (first, "one.png"), (second, "two.png"));

        await rig.RunRelayAsync();

        var alert = Assert.Single(rig.Service.Received).AlertFor(bobsBrowser);
        Assert.Equal("Sent 2 files", alert.GetProperty("body").GetString());
    }

    [Fact]
    public async Task A_long_message_is_cut_for_the_alert()
    {
        var (alice, bob, _, team) = await TeamAsync();
        var bobsBrowser = await SubscribeAsync(bob);
        using var rig = NewRig();
        using var client = rig.ClientFor(alice);
        await client.SendMessageAsync(team, new string('x', 900));

        await rig.RunRelayAsync();

        var body = Assert.Single(rig.Service.Received).AlertFor(bobsBrowser).GetProperty("body").GetString()!;
        Assert.Equal(new string('x', 100) + "…", body);
    }

    [Fact]
    public async Task A_member_who_muted_the_conversation_is_not_alerted_for_a_message()
    {
        var (alice, bob, _, team) = await TeamAsync();
        await SubscribeAsync(bob);
        using (var bobClient = _factory.CreateClientFor(bob))
            await bobClient.SetMutedAsync(team, true);
        using var rig = NewRig();
        using var client = rig.ClientFor(alice);
        await client.SendMessageAsync(team, "hello team");

        await rig.RunRelayAsync();

        Assert.Empty(rig.Service.Received);
    }

    [Fact]
    public async Task A_member_who_left_the_conversation_is_not_alerted()
    {
        var (alice, bob, _, team) = await TeamAsync();
        await SubscribeAsync(bob);
        await _factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == team && m.UserId == bob.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(m => m.LeftAt, DateTime.UtcNow)));
        using var rig = NewRig();
        using var client = rig.ClientFor(alice);
        await client.SendMessageAsync(team, "hello team");

        await rig.RunRelayAsync();

        Assert.Empty(rig.Service.Received);
    }

    [Fact]
    public async Task A_deactivated_member_is_not_alerted()
    {
        var (alice, bob, _, team) = await TeamAsync();
        await SubscribeAsync(bob);
        await _factory.DeactivateAsync(bob);
        using var rig = NewRig();
        using var client = rig.ClientFor(alice);
        await client.SendMessageAsync(team, "hello team");

        await rig.RunRelayAsync();

        Assert.Empty(rig.Service.Received);
    }

    [Fact]
    public async Task Somebody_in_another_conversation_is_not_alerted()
    {
        var (alice, _, _, team) = await TeamAsync();
        var stranger = await _factory.RegisterUserAsync("stranger");
        var friend = await _factory.RegisterUserAsync("friend");
        await SubscribeAsync(stranger);
        await SubscribeAsync(friend);
        using (var strangerClient = _factory.CreateClientFor(stranger))
            await strangerClient.CreateGroupAsync("Elsewhere", friend.Id);
        using var rig = NewRig();
        using var client = rig.ClientFor(alice);
        await client.SendMessageAsync(team, "hello team");

        await rig.RunRelayAsync();

        Assert.Empty(rig.Service.Received);
    }

    [Fact]
    public async Task A_message_that_was_deleted_before_the_alert_alerts_nobody_and_the_row_is_finished()
    {
        var (alice, bob, _, team) = await TeamAsync();
        await SubscribeAsync(bob);
        using var rig = NewRig();
        using var client = rig.ClientFor(alice);
        var message = await client.SendMessageAsync(team, "oops");
        await client.DeleteMessageAsync(team, message.MessageIdOf());

        await rig.RunRelayAsync();

        Assert.Empty(rig.Service.Received);
        var row = (await _factory.OutboxRowsAboutAsync("messageId", message.MessageIdOf())).Single(r => r.Kind == "MessageAlertRequested");
        Assert.NotNull(row.DispatchedAt);
    }

    // ##### Who has a live connection #####

    [Fact]
    public async Task A_person_with_a_live_connection_is_not_alerted_because_the_page_shows_it()
    {
        var (alice, bob, _, team) = await TeamAsync();
        await SubscribeAsync(bob);
        using var rig = NewRig();
        await using var bobsPage = await HubClient.ConnectAsync(rig.Host, bob);
        using var client = rig.ClientFor(alice);
        await client.SendMessageAsync(team, "hello team");

        await rig.RunRelayAsync();

        Assert.Empty(rig.Service.Received);
    }

    [Fact]
    public async Task A_person_who_closed_the_page_is_alerted()
    {
        var (alice, bob, _, team) = await TeamAsync();
        var bobsBrowser = await SubscribeAsync(bob);
        using var rig = NewRig();
        var bobsPage = await HubClient.ConnectAsync(rig.Host, bob);
        await bobsPage.StopAsync();
        await bobsPage.DisposeAsync();
        await WaitUntilNotConnectedAsync(rig, bob);
        using var client = rig.ClientFor(alice);
        await client.SendMessageAsync(team, "hello team");

        await rig.RunRelayAsync();

        Assert.Single(rig.Service.ReceivedBy(bobsBrowser));
    }

    // The wait before a person is announced offline keeps them online for their contacts, but a push cannot go over a connection that
    // is gone, so it is not a reason to withhold the alert.
    [Fact]
    public async Task A_person_who_closed_the_page_a_moment_ago_is_alerted_although_they_still_show_as_online()
    {
        var (alice, bob, _, team) = await TeamAsync();
        var bobsBrowser = await SubscribeAsync(bob);
        using var rig = NewRig(new Dictionary<string, string?> { ["Presence:OfflineGrace"] = "00:00:30" });
        var bobsPage = await HubClient.ConnectAsync(rig.Host, bob);
        await bobsPage.StopAsync();
        await bobsPage.DisposeAsync();
        await WaitUntilNotConnectedAsync(rig, bob);
        var snapshot = await rig.Host.Services.GetRequiredService<IPresenceStore>().GetSnapshotAsync([bob.Id], default);
        Assert.Contains(bob.Id, snapshot.Online);
        using var client = rig.ClientFor(alice);
        await client.SendMessageAsync(team, "hello team");

        await rig.RunRelayAsync();

        Assert.Single(rig.Service.ReceivedBy(bobsBrowser));
    }

    private static async Task WaitUntilNotConnectedAsync(Rig rig, TestUser user)
    {
        var presence = rig.Host.Services.GetRequiredService<IPresenceStore>();
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until && (await presence.GetConnectedAsync([user.Id], default)).Contains(user.Id))
            await Task.Delay(25);

        Assert.DoesNotContain(user.Id, await presence.GetConnectedAsync([user.Id], default));
    }

    // The relay asks Redis who is connected. When it cannot be asked it is not known who is away, so nothing is sent (someone who is
    // connected would be alerted twice) and nothing is lost: the row fails with the reason and is made when Redis is back.
    [Fact]
    public async Task When_presence_cannot_be_asked_the_alert_waits_and_is_made_when_it_can()
    {
        var (alice, bob, _, team) = await TeamAsync();
        var bobsBrowser = await SubscribeAsync(bob);
        var presence = new SwitchablePresenceStore();
        using var rig = NewRig(configureServices: services =>
        {
            services.RemoveAll<IPresenceStore>();
            services.AddSingleton<IPresenceStore>(provider =>
            {
                presence.Real = ActivatorUtilities.CreateInstance<PresenceStore>(provider);
                return presence;
            });
        });
        using var client = rig.ClientFor(alice);
        var message = await client.SendMessageAsync(team, "hello team");

        presence.Down = true;
        await rig.RunRelayAsync();

        Assert.Empty(rig.Service.Received);
        var row = (await _factory.OutboxRowsAboutAsync("messageId", message.MessageIdOf())).Single(r => r.Kind == "MessageAlertRequested");
        Assert.Null(row.DispatchedAt);
        Assert.Equal(1, row.Attempts);
        Assert.StartsWith("TimeoutException:", row.LastError);

        presence.Down = false;
        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        await rig.RunRelayAsync();

        Assert.Single(rig.Service.ReceivedBy(bobsBrowser));
        Assert.NotNull((await _factory.OutboxRowAsync(row.Id)).DispatchedAt);
    }

    // The real store, except that asking who is connected fails while it is down, the way a Redis that cannot be reached does.
    private sealed class SwitchablePresenceStore : IPresenceStore
    {
        public IPresenceStore Real { get; set; } = null!;
        public bool Down { get; set; }

        public Task<IReadOnlySet<Guid>> GetConnectedAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken) =>
            Down ? throw new TimeoutException("Redis did not answer.") : Real.GetConnectedAsync(userIds, cancellationToken);

        public Task<TouchResult> TouchAsync(Guid userId, string connectionId, CancellationToken cancellationToken) =>
            Real.TouchAsync(userId, connectionId, cancellationToken);

        public Task<IReadOnlyList<TouchResult>> TouchManyAsync(
            IReadOnlyList<(Guid UserId, string ConnectionId)> connections, CancellationToken cancellationToken) =>
            Real.TouchManyAsync(connections, cancellationToken);

        public Task<SettleResult> CloseAsync(Guid userId, string connectionId, TimeSpan grace, CancellationToken cancellationToken) =>
            Real.CloseAsync(userId, connectionId, grace, cancellationToken);

        public Task<SettleResult> FinishAsync(Guid userId, CancellationToken cancellationToken) =>
            Real.FinishAsync(userId, cancellationToken);

        public Task<ExpiredUsers> GetExpiredAsync(int max, CancellationToken cancellationToken) =>
            Real.GetExpiredAsync(max, cancellationToken);

        public Task<PresenceSnapshot> GetSnapshotAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken) =>
            Real.GetSnapshotAsync(userIds, cancellationToken);
    }

    // ##### Mentions #####

    [Fact]
    public async Task A_person_named_in_a_message_is_alerted_by_the_mention_and_not_also_by_the_message()
    {
        var (alice, bob, carol, team) = await TeamAsync();
        var bobsBrowser = await SubscribeAsync(bob);
        var carolsBrowser = await SubscribeAsync(carol);
        using var rig = NewRig();
        using var client = rig.ClientFor(alice);
        var message = await client.SendMessageAsync(team, $"@{bob.UserName} are you coming?");

        await rig.RunRelayAsync();

        var forBob = Assert.Single(rig.Service.ReceivedBy(bobsBrowser)).AlertFor(bobsBrowser);
        Assert.Equal("mention", forBob.GetProperty("type").GetString());
        Assert.Equal($"{alice.DisplayName} mentioned you in Team", forBob.GetProperty("title").GetString());
        Assert.Equal($"@{bob.UserName} are you coming?", forBob.GetProperty("body").GetString());
        Assert.Equal(message.MessageIdOf(), forBob.GetProperty("messageId").GetGuid());

        // The others get the message as before.
        var forCarol = Assert.Single(rig.Service.ReceivedBy(carolsBrowser)).AlertFor(carolsBrowser);
        Assert.Equal("message", forCarol.GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_mention_alerts_in_a_muted_conversation_and_a_message_does_not()
    {
        var (alice, bob, _, team) = await TeamAsync();
        var bobsBrowser = await SubscribeAsync(bob);
        using (var bobClient = _factory.CreateClientFor(bob))
            await bobClient.SetMutedAsync(team, true);
        using var rig = NewRig();
        using var client = rig.ClientFor(alice);
        await client.SendMessageAsync(team, "just talking");
        await client.SendMessageAsync(team, $"@{bob.UserName} you in?");

        await rig.RunRelayAsync();

        var push = Assert.Single(rig.Service.Received);
        Assert.Equal("mention", push.AlertFor(bobsBrowser).GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_mention_added_by_an_edit_alerts_the_person_it_names()
    {
        var (alice, bob, _, team) = await TeamAsync();
        var bobsBrowser = await SubscribeAsync(bob);
        using var rig = NewRig();
        using var client = rig.ClientFor(alice);
        var message = await client.SendMessageAsync(team, "hello");
        await rig.RunRelayAsync();
        var before = rig.Service.Received.Count;

        await client.EditMessageAsync(team, message.MessageIdOf(), $"hello @{bob.UserName}");
        await rig.RunRelayAsync();

        var pushes = rig.Service.ReceivedBy(bobsBrowser);
        Assert.Equal(before + 1, rig.Service.Received.Count);
        Assert.Equal("mention", pushes[^1].AlertFor(bobsBrowser).GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_mention_that_was_read_before_the_alert_is_not_alerted()
    {
        var (alice, bob, _, team) = await TeamAsync();
        await SubscribeAsync(bob);
        using var rig = NewRig();
        using var client = rig.ClientFor(alice);
        await client.SendMessageAsync(team, $"@{bob.UserName} you in?");
        using (var bobClient = _factory.CreateClientFor(bob))
            (await bobClient.MarkNotificationsReadResponseAsync()).EnsureSuccessStatusCode();

        await rig.RunRelayAsync();

        Assert.Empty(rig.Service.Received);
    }

    [Fact]
    public async Task A_mention_that_was_dismissed_before_the_alert_is_not_alerted_and_the_row_is_finished()
    {
        var (alice, bob, _, team) = await TeamAsync();
        await SubscribeAsync(bob);
        using var rig = NewRig();
        using var client = rig.ClientFor(alice);
        var message = await client.SendMessageAsync(team, $"@{bob.UserName} you in?");
        var notification = await _factory.QueryDbAsync(db =>
            db.Notifications.AsNoTracking().SingleAsync(n => n.MessageId == message.MessageIdOf()));
        using (var bobClient = _factory.CreateClientFor(bob))
            (await bobClient.DismissNotificationResponseAsync(notification.Id)).EnsureSuccessStatusCode();

        await rig.RunRelayAsync();

        Assert.Empty(rig.Service.Received);
        var row = (await _factory.OutboxRowsAboutAsync("notificationId", notification.Id)).Single(r => r.Kind == "NotificationAlertRequested");
        Assert.NotNull(row.DispatchedAt);
    }

    // ##### Invitations #####

    [Fact]
    public async Task A_person_added_when_a_group_is_made_is_alerted_if_they_are_away()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var bobsBrowser = await SubscribeAsync(bob);
        using var rig = NewRig();
        using var client = rig.ClientFor(alice);

        await client.CreateGroupAsync("Book club", bob.Id);
        await rig.RunRelayAsync();

        var alert = Assert.Single(rig.Service.Received).AlertFor(bobsBrowser);
        Assert.Equal("invitation", alert.GetProperty("type").GetString());
        Assert.Equal($"{alice.DisplayName} added you to Book club", alert.GetProperty("title").GetString());
        Assert.Equal(string.Empty, alert.GetProperty("body").GetString());
    }

    [Fact]
    public async Task A_person_added_to_a_group_later_is_alerted_if_they_are_away()
    {
        var (alice, _, _, team) = await TeamAsync();
        var dave = await _factory.RegisterUserAsync("dave");
        var davesBrowser = await SubscribeAsync(dave);
        using var rig = NewRig();
        using var client = rig.ClientFor(alice);

        await client.AddMembersAsync(team, dave.Id);
        await rig.RunRelayAsync();

        var alert = Assert.Single(rig.Service.Received).AlertFor(davesBrowser);
        Assert.Equal($"{alice.DisplayName} added you to Team", alert.GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_person_added_to_a_group_who_has_a_live_connection_is_not_alerted()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        await SubscribeAsync(bob);
        using var rig = NewRig();
        await using var bobsPage = await HubClient.ConnectAsync(rig.Host, bob);
        using var client = rig.ClientFor(alice);

        await client.CreateGroupAsync("Book club", bob.Id);
        await rig.RunRelayAsync();

        Assert.Empty(rig.Service.Received);
    }

    // ##### What the push services answer #####

    [Fact]
    public async Task A_browser_the_push_service_says_is_gone_is_removed_and_the_others_are_still_alerted()
    {
        var (alice, bob, _, team) = await TeamAsync();
        var gone = await SubscribeAsync(bob);
        var kept = await SubscribeAsync(bob);
        using var rig = NewRig();
        rig.Service.Answer(gone, HttpStatusCode.Gone);
        using var client = rig.ClientFor(alice);
        var message = await client.SendMessageAsync(team, "hello team");

        await rig.RunRelayAsync();

        var remaining = await _factory.SubscriptionsOfAsync(bob.Id);
        Assert.Equal([kept.Endpoint], remaining.Select(s => s.Endpoint));
        Assert.Single(rig.Service.ReceivedBy(kept));
        var row = (await _factory.OutboxRowsAboutAsync("messageId", message.MessageIdOf())).Single(r => r.Kind == "MessageAlertRequested");
        Assert.NotNull(row.DispatchedAt);
    }

    [Fact]
    public async Task A_browser_that_was_reached_is_marked_as_used()
    {
        var (alice, bob, _, team) = await TeamAsync();
        var browser = Browser.Subscribe();
        var oldTime = DateTime.UtcNow.AddDays(-30);
        await _factory.AddSubscriptionAsync(bob.Id, browser, lastUsedAt: oldTime);
        using var rig = NewRig();
        using var client = rig.ClientFor(alice);
        await client.SendMessageAsync(team, "hello team");

        await rig.RunRelayAsync();

        var stored = Assert.Single(await _factory.SubscriptionsOfAsync(bob.Id));
        Assert.True(stored.LastUsedAt > DateTime.UtcNow.AddMinutes(-5), "The browser that was reached is still marked as last used a month ago.");
    }

    [Fact]
    public async Task A_refusal_is_logged_and_not_retried_and_the_registration_is_kept()
    {
        var (alice, bob, _, team) = await TeamAsync();
        var browser = await SubscribeAsync(bob);
        using var rig = NewRig();
        rig.Service.Respond = FakePushService.Answer(HttpStatusCode.Forbidden);
        using var client = rig.ClientFor(alice);
        var message = await client.SendMessageAsync(team, "hello team");

        await rig.RunRelayAsync();
        rig.Clock.Advance(TimeSpan.FromHours(1));
        await rig.RunRelayAsync();

        Assert.Single(rig.Service.ReceivedBy(browser));
        Assert.Single(await _factory.SubscriptionsOfAsync(bob.Id));
        Assert.Contains(rig.Log.Entries, e => e.StartsWith("[Warning]") && e.Contains("403"));
        var row = (await _factory.OutboxRowsAboutAsync("messageId", message.MessageIdOf())).Single(r => r.Kind == "MessageAlertRequested");
        Assert.NotNull(row.DispatchedAt);
    }

    [Fact]
    public async Task A_push_service_that_is_down_makes_the_relay_try_again_and_the_browser_that_was_reached_gets_it_again()
    {
        var (alice, bob, _, team) = await TeamAsync();
        var reachable = await SubscribeAsync(bob);
        var down = await SubscribeAsync(bob);
        using var rig = NewRig();
        rig.Service.Answer(down, HttpStatusCode.ServiceUnavailable);
        using var client = rig.ClientFor(alice);
        var message = await client.SendMessageAsync(team, "hello team");

        await rig.RunRelayAsync();

        // The row stays, with what went wrong (a count, nothing of the alert), and the next attempt is later.
        var row = (await _factory.OutboxRowsAboutAsync("messageId", message.MessageIdOf())).Single(r => r.Kind == "MessageAlertRequested");
        Assert.Null(row.DispatchedAt);
        Assert.Equal(1, row.Attempts);
        Assert.Equal("PushDeliveryException: 1 browsers could not be reached by their push service.", row.LastError);
        Assert.Single(rig.Service.ReceivedBy(reachable));
        Assert.Single(await _factory.SubscriptionsOfAsync(bob.Id), s => s.Endpoint == down.Endpoint);

        // The push service comes back.
        rig.Service.Respond = FakePushService.Answer(HttpStatusCode.Created);
        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        await rig.RunRelayAsync();

        // The one that was down was tried twice (503, then 201); the one that was reached got the alert twice.
        Assert.Equal(2, rig.Service.ReceivedBy(down).Count);
        Assert.Equal(2, rig.Service.ReceivedBy(reachable).Count);
        Assert.NotNull((await _factory.OutboxRowAsync(row.Id)).DispatchedAt);
    }

    [Fact]
    public async Task An_alert_is_given_up_after_the_attempts_the_outbox_allows()
    {
        var (alice, bob, _, team) = await TeamAsync();
        await SubscribeAsync(bob);
        using var rig = NewRig(new Dictionary<string, string?> { ["Outbox:MaxAttempts"] = "2" });
        rig.Service.Respond = FakePushService.Answer(HttpStatusCode.ServiceUnavailable);
        using var client = rig.ClientFor(alice);
        var message = await client.SendMessageAsync(team, "hello team");

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await rig.RunRelayAsync();
            rig.Clock.Advance(TimeSpan.FromHours(1));
        }

        Assert.Equal(2, rig.Service.Received.Count);
        var row = (await _factory.OutboxRowsAboutAsync("messageId", message.MessageIdOf())).Single(r => r.Kind == "MessageAlertRequested");
        Assert.Null(row.DispatchedAt);
        Assert.Equal(2, row.Attempts);
    }

    [Fact]
    public async Task An_alert_is_sent_to_no_more_browsers_at_once_than_the_limit()
    {
        var (alice, bob, _, team) = await TeamAsync();
        for (var i = 0; i < 6; i++)
            await SubscribeAsync(bob);
        using var rig = NewRig(new Dictionary<string, string?> { ["Push:MaxParallelSends"] = "2", ["Push:MaxSubscriptionsPerUser"] = "10" });
        rig.Service.Delay = TimeSpan.FromMilliseconds(150);
        using var client = rig.ClientFor(alice);
        await client.SendMessageAsync(team, "hello team");

        await rig.RunRelayAsync();

        Assert.Equal(6, rig.Service.Received.Count);
        Assert.Equal(2, rig.Service.MostInFlight);
    }

    [Fact]
    public async Task Browsers_are_reached_together_and_not_one_after_another()
    {
        var (alice, bob, _, team) = await TeamAsync();
        for (var i = 0; i < 4; i++)
            await SubscribeAsync(bob);
        using var rig = NewRig();
        rig.Service.Delay = TimeSpan.FromMilliseconds(150);
        using var client = rig.ClientFor(alice);
        await client.SendMessageAsync(team, "hello team");

        await rig.RunRelayAsync();

        Assert.True(rig.Service.MostInFlight > 1, "The pushes were sent one at a time.");
    }

    // ##### When push has been switched off #####

    [Fact]
    public async Task Alert_rows_left_from_before_push_was_switched_off_are_finished_without_sending_anything()
    {
        var (alice, bob, _, team) = await TeamAsync();
        await SubscribeAsync(bob);
        using var withKeys = NewRig();
        using var client = withKeys.ClientFor(alice);
        var message = await client.SendMessageAsync(team, "hello team");

        // The same database, a host without keys.
        var service = new FakePushService();
        using var withoutKeys = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient<IPushSender, WebPushSender>().ConfigurePrimaryHttpMessageHandler(() => service)));
        await withoutKeys.Services.GetRequiredService<OutboxRelayService>().RunOnceAsync(CancellationToken.None);

        Assert.Empty(service.Received);
        var row = (await _factory.OutboxRowsAboutAsync("messageId", message.MessageIdOf())).Single(r => r.Kind == "MessageAlertRequested");
        Assert.NotNull(row.DispatchedAt);
    }

    // ##### What is logged #####

    [Fact]
    public async Task Nothing_of_the_addresses_the_keys_or_the_words_is_logged()
    {
        var (alice, bob, _, team) = await TeamAsync();
        var browser = await SubscribeAsync(bob);
        using var rig = NewRig();
        using var client = rig.ClientFor(alice);
        var words = $"words-{Guid.NewGuid():N}";
        await client.SendMessageAsync(team, words);

        await rig.RunRelayAsync();

        var log = string.Join("\n", rig.Log.Entries);
        Assert.Contains("sent to 1 of 1 browsers", log);
        Assert.DoesNotContain(words, log);
        Assert.DoesNotContain(browser.Endpoint, log);
        Assert.DoesNotContain(new Uri(browser.Endpoint).AbsolutePath, log);
        Assert.DoesNotContain(browser.P256dh, log);
        Assert.DoesNotContain(browser.Auth, log);
        Assert.DoesNotContain(PushApi.Vapid.PrivateKey, log);
    }
}
