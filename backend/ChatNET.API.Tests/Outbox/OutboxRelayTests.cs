using System.Text.Json;
using ChatNET.API.Common.Persistence;
using ChatNET.API.Media;
using ChatNET.API.Messaging.Hubs;
using ChatNET.API.Messaging.Models;
using ChatNET.API.Outbox;
using ChatNET.API.Outbox.Models;
using ChatNET.API.Tests.Infrastructure;
using ChatNET.API.Tests.Media;
using ChatNET.API.Tests.Messaging;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ChatNET.API.Tests.Outbox;

// The relay makes the pushes that the requests did not: the request's own push failed, or never happened. Each test
// takes two hosts that share the database and Redis. One has a hub whose pushes fail, so the requests that go through
// it leave their rows waiting; the other runs the relay with a clock the test moves, and real people are connected to
// the first host's hub to see what arrives (it reaches them through the Redis backplane, as it does between servers).
[Collection(IntegrationTestCollection.Name)]
public class OutboxRelayTests : IAsyncLifetime
{
    private readonly ChatApiFactory _factory;

    public OutboxRelayTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // Rows other tests left waiting would be pushed by the passes these tests run.
    public Task InitializeAsync() => _factory.RemoveWaitingOutboxRowsAsync();

    public Task DisposeAsync() => _factory.RemoveWaitingOutboxRowsAsync();

    private WebApplicationFactory<Program> BrokenHub() =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHubContext<ChatHub, IChatClient>>();
            services.AddSingleton<IHubContext<ChatHub, IChatClient>>(new ThrowingHubContext());
        }));

    private static HttpClient Authorized(WebApplicationFactory<Program> host, TestUser user)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", user.Token);
        return client;
    }

    // The relay of a host whose clock is the given one.
    private static OutboxRelayService RelayOf(WebApplicationFactory<Program> host) =>
        host.Services.GetRequiredService<OutboxRelayService>();

    private Task RunAsync(WebApplicationFactory<Program> host) => RelayOf(host).RunOnceAsync(CancellationToken.None);

    // Puts a row straight into the table, for the states the application does not produce by itself.
    private async Task<OutboxMessage> InsertAsync(
        IOutboxEvent outboxEvent, DateTime createdAt, DateTime nextAttemptAt, DateTime? dispatchedAt = null, int attempts = 0)
    {
        var row = new OutboxMessage
        {
            // Random, so the order of the ids says nothing about the order the rows were made in.
            Id = Guid.NewGuid(),
            Kind = outboxEvent.Kind,
            Payload = OutboxEvents.Serialize(outboxEvent),
            CreatedAt = createdAt,
            NextAttemptAt = nextAttemptAt,
            DispatchedAt = dispatchedAt,
            Attempts = attempts,
        };
        await _factory.ExecuteDbAsync(async db =>
        {
            db.OutboxMessages.Add(row);
            await db.SaveChangesAsync();
        });

        return row;
    }

    // ##### What the relay delivers #####

    [Fact]
    public async Task A_message_whose_push_failed_reaches_the_conversation_through_the_relay()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await using var bobTab = await HubClient.ConnectAsync(_factory, bob);
        var clock = new ManualTimeProvider();
        using var relayHost = _factory.WithClock(clock);
        using var broken = BrokenHub();
        using var client = Authorized(broken, alice);

        var response = await client.PostMessageAsync(conversation, "you will get this late");
        var sent = await response.ReadJsonAsync();
        Assert.False(await bobTab.ReceivedMessageAsync(conversation));

        clock.Advance(TimeSpan.FromMinutes(3));
        await RunAsync(relayHost);

        var pushed = await bobTab.WaitForMessageAsync(conversation, sent.MessageIdOf());
        Assert.Equal("you will get this late", pushed.ContentOf());
        Assert.Equal(sent.GetProperty("senderDisplayName").GetString(), pushed.GetProperty("senderDisplayName").GetString());
        Assert.Equal(sent.GetProperty("createdAt").GetDateTime(), pushed.GetProperty("createdAt").GetDateTime());
        var row = Assert.Single(await _factory.OutboxRowsAboutAsync("messageId", sent.MessageIdOf()));
        Assert.NotNull(row.DispatchedAt);
        Assert.Equal(1, row.Attempts);
    }

    [Fact]
    public async Task The_relay_leaves_a_row_alone_until_the_request_has_had_its_time()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await using var bobTab = await HubClient.ConnectAsync(_factory, bob);
        var clock = new ManualTimeProvider();
        using var relayHost = _factory.WithClock(clock);
        using var broken = BrokenHub();
        using var client = Authorized(broken, alice);
        var sent = await (await client.PostMessageAsync(conversation, "wait for it")).ReadJsonAsync();

        clock.Advance(TimeSpan.FromMinutes(1));
        await RunAsync(relayHost);

        Assert.False(await bobTab.ReceivedMessageAsync(conversation));
        var waiting = Assert.Single(await _factory.OutboxRowsAboutAsync("messageId", sent.MessageIdOf()));
        Assert.Null(waiting.DispatchedAt);
        Assert.Equal(0, waiting.Attempts);

        clock.Advance(TimeSpan.FromMinutes(2));
        await RunAsync(relayHost);

        await bobTab.WaitForMessageAsync(conversation, sent.MessageIdOf());
    }

    [Fact]
    public async Task An_edit_whose_push_failed_reaches_the_conversation_through_the_relay()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        using var normal = _factory.CreateClientFor(alice);
        var sent = await normal.SendMessageAsync(conversation, "first draft");
        await using var bobTab = await HubClient.ConnectAsync(_factory, bob);
        var clock = new ManualTimeProvider();
        using var relayHost = _factory.WithClock(clock);
        using var broken = BrokenHub();
        using var client = Authorized(broken, alice);

        await client.EditMessageAsync(conversation, sent.MessageIdOf(), "second draft");
        Assert.False(await bobTab.ReceivedUpdateAsync(sent.MessageIdOf()));

        clock.Advance(TimeSpan.FromMinutes(3));
        await RunAsync(relayHost);

        var pushed = await bobTab.WaitForUpdateAsync(sent.MessageIdOf());
        Assert.Equal("second draft", pushed.ContentOf());
        Assert.Equal(1, pushed.GetProperty("editCount").GetInt32());
    }

    // The row holds ids, so a push made late says what is true then. A message edited and then deleted while the pushes
    // were failing goes out as deleted both times, with none of the text it had.
    [Fact]
    public async Task A_late_push_of_an_edited_and_then_deleted_message_carries_none_of_its_old_text()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        using var normal = _factory.CreateClientFor(alice);
        var sent = await normal.SendMessageAsync(conversation, "the first words");
        await using var bobTab = await HubClient.ConnectAsync(_factory, bob);
        var clock = new ManualTimeProvider();
        using var relayHost = _factory.WithClock(clock);
        using var broken = BrokenHub();
        using var client = Authorized(broken, alice);
        await client.EditMessageAsync(conversation, sent.MessageIdOf(), "the edited words");
        await client.DeleteMessageAsync(conversation, sent.MessageIdOf());

        clock.Advance(TimeSpan.FromMinutes(3));
        await RunAsync(relayHost);

        await bobTab.WaitForUpdateAsync(m => m.GetProperty("id").GetGuid() == sent.MessageIdOf() && m.GetProperty("isDeleted").GetBoolean());
        Assert.False(await bobTab.ReceivedUpdateAsync(Guid.Empty));
        Assert.All(bobTab.Updates.Where(m => m.GetProperty("id").GetGuid() == sent.MessageIdOf()), pushed =>
        {
            Assert.True(pushed.GetProperty("isDeleted").GetBoolean());
            Assert.DoesNotContain("words", pushed.GetRawText());
        });
    }

    [Fact]
    public async Task A_mention_whose_push_failed_reaches_the_person_through_the_relay()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var setup = _factory.CreateClientFor(alice);
        var group = (await setup.CreateGroupAsync("Mentions", bob.Id)).IdOf();
        await using var bobTab = await HubClient.ConnectAsync(_factory, bob);
        var clock = new ManualTimeProvider();
        using var relayHost = _factory.WithClock(clock);
        using var broken = BrokenHub();
        using var client = Authorized(broken, alice);
        var sent = await (await client.PostMessageAsync(group, $"ping @{bob.UserName}")).ReadJsonAsync();
        Assert.False(await bobTab.ReceivedNotificationAsync(n => MessageIdOf(n) == sent.MessageIdOf()));

        clock.Advance(TimeSpan.FromMinutes(3));
        await RunAsync(relayHost);

        var notification = await bobTab.WaitForNotificationAsync(n => MessageIdOf(n) == sent.MessageIdOf());
        Assert.Equal("Mention", notification.GetProperty("type").GetString());
        Assert.Equal(alice.DisplayName, notification.GetProperty("actorDisplayName").GetString());
    }

    [Fact]
    public async Task A_new_group_whose_announcement_failed_reaches_its_members_through_the_relay()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        await using var bobTab = await HubClient.ConnectAsync(_factory, bob);
        var clock = new ManualTimeProvider();
        using var relayHost = _factory.WithClock(clock);
        using var broken = BrokenHub();
        using var client = Authorized(broken, alice);
        var created = await (await client.PostGroupAsync("Late news", bob.Id)).ReadJsonAsync();
        var group = created.IdOf();
        Assert.False(await bobTab.ReceivedAddedAsync(group));

        clock.Advance(TimeSpan.FromMinutes(3));
        await RunAsync(relayHost);

        var added = await bobTab.WaitForAddedAsync(group);
        Assert.Equal("Late news", added.GetProperty("name").GetString());
        var invitation = await bobTab.WaitForNotificationAsync(n =>
            n.GetProperty("type").GetString() == "MemberAdded"
            && n.GetProperty("conversationId").GetGuid() == group);
        Assert.Equal(alice.DisplayName, invitation.GetProperty("actorDisplayName").GetString());
    }

    [Fact]
    public async Task A_new_direct_conversation_is_told_to_each_side_in_its_own_words_through_the_relay()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        await using var aliceTab = await HubClient.ConnectAsync(_factory, alice);
        await using var bobTab = await HubClient.ConnectAsync(_factory, bob);
        var clock = new ManualTimeProvider();
        using var relayHost = _factory.WithClock(clock);
        using var broken = BrokenHub();
        using var client = Authorized(broken, alice);
        var conversation = (await (await client.PostDirectAsync(bob.Id)).ReadJsonAsync()).IdOf();

        clock.Advance(TimeSpan.FromMinutes(3));
        await RunAsync(relayHost);

        Assert.Equal(bob.DisplayName, (await aliceTab.WaitForAddedAsync(conversation)).GetProperty("name").GetString());
        Assert.Equal(alice.DisplayName, (await bobTab.WaitForAddedAsync(conversation)).GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_read_position_whose_push_failed_reaches_the_conversation_through_the_relay()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        var sent = await aliceClient.SendMessageAsync(conversation, "read me");
        await using var aliceTab = await HubClient.ConnectAsync(_factory, alice);
        var clock = new ManualTimeProvider();
        using var relayHost = _factory.WithClock(clock);
        using var broken = BrokenHub();
        using var bobClient = Authorized(broken, bob);

        await bobClient.MarkReadAsync(conversation, sent.MessageIdOf());
        Assert.False(await aliceTab.ReceivedReadAsync(r => r.UserId == bob.Id));

        clock.Advance(TimeSpan.FromMinutes(3));
        await RunAsync(relayHost);

        var read = await aliceTab.WaitForReadAsync(r => r.UserId == bob.Id);
        Assert.Equal(conversation, read.ConversationId);
        Assert.Equal(sent.GetProperty("createdAt").GetDateTime(), read.ReadAt);
    }

    // The row holds ids, so the push is built from what is stored when it is made: links signed now, not when the
    // message was sent.
    [Fact]
    public async Task A_message_with_a_file_is_pushed_with_a_link_that_works()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await using var bobTab = await HubClient.ConnectAsync(_factory, bob);
        var clock = new ManualTimeProvider();
        using var relayHost = _factory.WithClock(clock);
        using var broken = BrokenHub();
        using var client = Authorized(broken, alice);
        var key = await client.UploadFileAsync(conversation, "hello"u8.ToArray(), "text/plain", "note.txt");
        var sent = await (await client.PostMessageWithFilesAsync(conversation, "see file", [(key, "note.txt")])).ReadJsonAsync();

        clock.Advance(TimeSpan.FromMinutes(3));
        await RunAsync(relayHost);

        var pushed = await bobTab.WaitForMessageAsync(conversation, sent.MessageIdOf());
        var attachment = Assert.Single(pushed.AttachmentsOf());
        Assert.Equal("note.txt", attachment.GetProperty("fileName").GetString());
        var download = await MediaApi.FollowAsync(attachment);
        Assert.Equal("hello", await download.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_row_whose_subject_is_gone_counts_as_done_and_pushes_nothing()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await using var bobTab = await HubClient.ConnectAsync(_factory, bob);
        var clock = new ManualTimeProvider();
        using var relayHost = _factory.WithClock(clock);
        using var broken = BrokenHub();
        using var client = Authorized(broken, alice);
        var sent = await (await client.PostMessageAsync(conversation, "now you see it")).ReadJsonAsync();
        await _factory.ExecuteDbAsync(db => db.Messages.Where(m => m.Id == sent.MessageIdOf()).ExecuteDeleteAsync());

        clock.Advance(TimeSpan.FromMinutes(3));
        await RunAsync(relayHost);

        Assert.False(await bobTab.ReceivedMessageAsync(conversation));
        var row = Assert.Single(await _factory.OutboxRowsAboutAsync("messageId", sent.MessageIdOf()));
        Assert.NotNull(row.DispatchedAt);
        Assert.Equal(1, row.Attempts);
        Assert.Null(row.LastError);
    }

    // ##### Failures and retries #####

    [Fact]
    public async Task A_push_the_relay_cannot_make_is_recorded_and_tried_again_with_a_growing_wait()
    {
        var capture = new CapturingLoggerProvider();
        var clock = new ManualTimeProvider();
        using var relayHost = _factory.WithClock(clock).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHubContext<ChatHub, IChatClient>>();
            services.AddSingleton<IHubContext<ChatHub, IChatClient>>(new ThrowingHubContext());
            services.Configure<OutboxOptions>(o =>
            {
                o.RetryDelay = TimeSpan.FromSeconds(10);
                o.MaxAttempts = 3;
            });
            services.AddLogging(logging => logging.AddProvider(capture));
        }));
        var start = clock.GetUtcNow().UtcDateTime;
        var row = await InsertAsync(
            new ConversationRead(Guid.NewGuid(), Guid.NewGuid(), start), start.AddMinutes(-1), start.AddMinutes(-1));

        await RunAsync(relayHost);

        var first = await _factory.OutboxRowAsync(row.Id);
        Assert.Equal(1, first.Attempts);
        Assert.Null(first.DispatchedAt);
        Assert.Contains("Simulated push failure", first.LastError);
        AssertAbout(TimeSpan.FromSeconds(10), first.NextAttemptAt - start);

        // Not due yet, so another pass leaves it alone.
        await RunAsync(relayHost);
        Assert.Equal(1, (await _factory.OutboxRowAsync(row.Id)).Attempts);

        clock.Advance(TimeSpan.FromSeconds(11));
        await RunAsync(relayHost);
        var second = await _factory.OutboxRowAsync(row.Id);
        Assert.Equal(2, second.Attempts);
        AssertAbout(TimeSpan.FromSeconds(20), second.NextAttemptAt - clock.GetUtcNow().UtcDateTime);

        clock.Advance(TimeSpan.FromSeconds(21));
        await RunAsync(relayHost);
        Assert.Equal(3, (await _factory.OutboxRowAsync(row.Id)).Attempts);
        Assert.Single(capture.Entries, e => e.StartsWith("[Warning]") && e.Contains("Gave up") && e.Contains(row.Id.ToString()));

        // Given up: later passes do not take it again, however long they wait.
        clock.Advance(TimeSpan.FromHours(2));
        await RunAsync(relayHost);
        var last = await _factory.OutboxRowAsync(row.Id);
        Assert.Equal(3, last.Attempts);
        Assert.Null(last.DispatchedAt);
    }

    [Fact]
    public async Task A_row_of_a_kind_the_relay_does_not_know_is_recorded_not_dropped()
    {
        var clock = new ManualTimeProvider();
        using var relayHost = _factory.WithClock(clock);
        var now = clock.GetUtcNow().UtcDateTime;
        var row = new OutboxMessage
        {
            Id = Guid.CreateVersion7(),
            Kind = "FromTheFuture",
            Payload = "{}",
            CreatedAt = now.AddMinutes(-1),
            NextAttemptAt = now.AddMinutes(-1),
        };
        await _factory.ExecuteDbAsync(async db =>
        {
            db.OutboxMessages.Add(row);
            await db.SaveChangesAsync();
        });

        await RunAsync(relayHost);

        var stored = await _factory.OutboxRowAsync(row.Id);
        Assert.Null(stored.DispatchedAt);
        Assert.Contains("FromTheFuture", stored.LastError);
    }

    [Fact]
    public async Task A_failed_pass_does_not_stop_the_rows_after_it()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var setup = _factory.CreateClientFor(alice);
        var conversation = (await setup.CreateDirectAsync(bob.Id)).IdOf();
        var counting = new CountingHubContext();
        var clock = new ManualTimeProvider();
        using var relayHost = _factory.WithClock(clock).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHubContext<ChatHub, IChatClient>>();
            services.AddSingleton<IHubContext<ChatHub, IChatClient>>(counting);
        }));
        var now = clock.GetUtcNow().UtcDateTime;
        var poisoned = new OutboxMessage
        {
            Id = Guid.CreateVersion7(),
            Kind = "FromTheFuture",
            Payload = "{}",
            CreatedAt = now.AddMinutes(-3),
            NextAttemptAt = now.AddMinutes(-3),
        };
        await _factory.ExecuteDbAsync(async db =>
        {
            db.OutboxMessages.Add(poisoned);
            await db.SaveChangesAsync();
        });
        var good = await InsertAsync(
            new ConversationRead(conversation, bob.Id, now), now.AddMinutes(-2), now.AddMinutes(-2));

        await RunAsync(relayHost);

        Assert.NotNull((await _factory.OutboxRowAsync(good.Id)).DispatchedAt);
        Assert.Single(counting.Reads, r => r.ConversationId == conversation);
    }

    // ##### Several relays #####

    // Every instance runs a relay. A row one of them has taken is held until its retry is due, and rows another is
    // taking at this very moment are skipped, so a row is pushed once however the passes interleave.
    [Fact]
    public async Task Two_relays_running_together_never_push_the_same_row_twice()
    {
        var counting = new CountingHubContext();
        var conversation = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var rows = new List<OutboxMessage>();
        for (var i = 0; i < 40; i++)
        {
            rows.Add(await InsertAsync(
                new ConversationRead(conversation, Guid.NewGuid(), now.AddMilliseconds(i)),
                now.AddMinutes(-1).AddMilliseconds(i), now.AddMinutes(-1)));
        }

        WebApplicationFactory<Program> NewRelayHost() =>
            _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHubContext<ChatHub, IChatClient>>();
                services.AddSingleton<IHubContext<ChatHub, IChatClient>>(counting);
                services.Configure<OutboxOptions>(o => o.BatchSize = 7);
            }));

        using var first = NewRelayHost();
        using var second = NewRelayHost();
        for (var pass = 0; pass < 20 && counting.Reads.Count(r => r.ConversationId == conversation) < rows.Count; pass++)
            await Task.WhenAll(RunAsync(first), RunAsync(second));

        Assert.Equal(rows.Count, counting.Reads.Count(r => r.ConversationId == conversation));
        var stored = await _factory.QueryDbAsync(db => db.OutboxMessages
            .Where(o => rows.Select(r => r.Id).Contains(o.Id))
            .ToListAsync());
        Assert.All(stored, row =>
        {
            Assert.NotNull(row.DispatchedAt);
            Assert.Equal(1, row.Attempts);
        });
    }

    // Messages must not be pushed out of order: the rows are taken oldest first, within a pass and from pass to pass.
    [Fact]
    public async Task The_relay_pushes_the_oldest_rows_first()
    {
        var counting = new CountingHubContext();
        var conversation = Guid.NewGuid();
        var start = DateTime.UtcNow.AddMinutes(-10);

        // Written newest first, so the table order is the opposite of the order they have to leave in.
        for (var i = 19; i >= 0; i--)
        {
            await InsertAsync(
                new ConversationRead(conversation, Guid.NewGuid(), start.AddSeconds(i)),
                start.AddSeconds(i), start.AddMinutes(-1));
        }

        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHubContext<ChatHub, IChatClient>>();
            services.AddSingleton<IHubContext<ChatHub, IChatClient>>(counting);
            services.Configure<OutboxOptions>(o => o.BatchSize = 7);
        }));
        for (var pass = 0; pass < 5; pass++)
            await RunAsync(host);

        var order = counting.Reads.Where(r => r.ConversationId == conversation).Select(r => r.ReadAt).ToList();
        Assert.Equal(20, order.Count);
        Assert.Equal(order.Order().ToList(), order);
    }

    // ##### Files of deleted messages #####

    private async Task<(Guid Conversation, Guid MessageId, MessageAttachment File)> MessageWithFileAsync(TestUser sender, Guid conversation)
    {
        using var client = _factory.CreateClientFor(sender);
        var key = await client.UploadFileAsync(conversation, "hello"u8.ToArray(), "text/plain", "note.txt");
        var sent = await client.SendMessageWithFilesAsync(conversation, "see file", (key, "note.txt"));
        var file = await _factory.QueryDbAsync(db => db.MessageAttachments.SingleAsync(a => a.MessageId == sent.MessageIdOf()));

        return (conversation, sent.MessageIdOf(), file);
    }

    // The files of a message people can still read must never go, whatever put the row there.
    [Fact]
    public async Task A_row_asking_for_the_files_of_a_message_that_is_not_deleted_removes_nothing()
    {
        var capture = new CapturingLoggerProvider();
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        var (_, messageId, file) = await MessageWithFileAsync(alice, conversation);
        var clock = new ManualTimeProvider();
        using var relayHost = _factory.WithClock(clock).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddLogging(logging => logging.AddProvider(capture))));
        var now = clock.GetUtcNow().UtcDateTime;
        var row = await InsertAsync(new MessageFilesRemoved(messageId), now.AddMinutes(-1), now.AddMinutes(-1));

        await RunAsync(relayHost);

        Assert.True(await _factory.ObjectExistsAsync(file.StorageKey));
        Assert.NotNull((await _factory.OutboxRowAsync(row.Id)).DispatchedAt);
        Assert.Single(capture.Entries, e => e.StartsWith("[Warning]") && e.Contains(row.Id.ToString()));
    }

    // A second run of the job (the request and the relay both did it, or the relay ran twice) finds nothing to remove.
    [Fact]
    public async Task A_file_that_is_already_gone_counts_as_removed()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        var (_, messageId, file) = await MessageWithFileAsync(alice, conversation);
        using var client = _factory.CreateClientFor(alice);
        await client.DeleteMessageAsync(conversation, messageId);
        Assert.False(await _factory.ObjectExistsAsync(file.StorageKey));
        var clock = new ManualTimeProvider();
        using var relayHost = _factory.WithClock(clock);
        var now = clock.GetUtcNow().UtcDateTime;
        var row = await InsertAsync(new MessageFilesRemoved(messageId), now.AddMinutes(-1), now.AddMinutes(-1));

        await RunAsync(relayHost);

        var done = await _factory.OutboxRowAsync(row.Id);
        Assert.NotNull(done.DispatchedAt);
        Assert.Null(done.LastError);
    }

    [Fact]
    public async Task Files_the_relay_cannot_remove_are_tried_again_and_removed_when_the_storage_works()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        var (_, messageId, file) = await MessageWithFileAsync(alice, conversation);
        await _factory.ExecuteDbAsync(db => db.Messages.Where(m => m.Id == messageId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsDeleted, true).SetProperty(m => m.Content, (string?)null)));
        var clock = new ManualTimeProvider();
        using var failing = _factory.WithClock(clock).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IBlobStorage>();
            services.AddSingleton<IBlobStorage>(provider =>
                new UndeletableBlobStorage(ActivatorUtilities.CreateInstance<S3BlobStorage>(provider)));
        }));
        using var working = _factory.WithClock(clock);
        var now = clock.GetUtcNow().UtcDateTime;
        var row = await InsertAsync(new MessageFilesRemoved(messageId), now.AddMinutes(-1), now.AddMinutes(-1));

        await RunAsync(failing);

        var first = await _factory.OutboxRowAsync(row.Id);
        Assert.Equal(1, first.Attempts);
        Assert.Null(first.DispatchedAt);
        Assert.Contains("could not be deleted", first.LastError);
        Assert.True(await _factory.ObjectExistsAsync(file.StorageKey));

        clock.Advance(TimeSpan.FromSeconds(6));
        await RunAsync(working);

        Assert.False(await _factory.ObjectExistsAsync(file.StorageKey));
        Assert.NotNull((await _factory.OutboxRowAsync(row.Id)).DispatchedAt);
    }

    // ##### Housekeeping #####

    [Fact]
    public async Task Finished_rows_are_removed_after_the_retention_and_waiting_ones_never_are()
    {
        var clock = new ManualTimeProvider();
        using var relayHost = _factory.WithClock(clock);
        var now = clock.GetUtcNow().UtcDateTime;
        var later = now.AddDays(1);
        var someone = new ConversationRead(Guid.NewGuid(), Guid.NewGuid(), now);

        var pushedLongAgo = await InsertAsync(someone, now.AddDays(-3), later, dispatchedAt: now.AddDays(-2), attempts: 1);
        var pushedJustNow = await InsertAsync(someone, now.AddMinutes(-5), later, dispatchedAt: now.AddMinutes(-1), attempts: 1);
        var waiting = await InsertAsync(someone, now, later);
        var gaveUpLongAgo = await InsertAsync(someone, now.AddDays(-2), later, attempts: 10);
        var retryingLongAgo = await InsertAsync(someone, now.AddDays(-2), later, attempts: 1);
        var gaveUpJustNow = await InsertAsync(someone, now.AddMinutes(-5), later, attempts: 10);

        await RunAsync(relayHost);

        var left = await _factory.QueryDbAsync(db => db.OutboxMessages
            .Where(o => new[] { pushedLongAgo.Id, pushedJustNow.Id, waiting.Id, gaveUpLongAgo.Id, retryingLongAgo.Id, gaveUpJustNow.Id }.Contains(o.Id))
            .Select(o => o.Id)
            .ToListAsync());
        Assert.Equal(
            new[] { pushedJustNow.Id, waiting.Id, retryingLongAgo.Id, gaveUpJustNow.Id }.Order().ToArray(),
            left.Order().ToArray());
    }

    // The relay is a hosted service: with a short interval it makes the pushes by itself, with nobody running a pass.
    [Fact]
    public async Task The_relay_runs_by_itself_on_its_interval()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var setup = _factory.CreateClientFor(alice);
        var group = (await setup.CreateGroupAsync("Timer", bob.Id)).IdOf();
        await using var aliceTab = await HubClient.ConnectAsync(_factory, alice);
        var now = DateTime.UtcNow;
        await InsertAsync(new ConversationRead(group, bob.Id, now), now.AddMinutes(-1), now.AddMinutes(-1));

        using var ticking = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Configure<OutboxOptions>(o => o.RelayInterval = TimeSpan.FromMilliseconds(200))));
        _ = ticking.Services;

        var read = await aliceTab.WaitForReadAsync(r => r.UserId == bob.Id);
        Assert.Equal(group, read.ConversationId);
    }

    // The relay looks at the rows that are waiting, not at the table: a day of finished rows is not stepped over.
    [Fact]
    public async Task The_claim_reads_only_the_rows_that_are_waiting_not_the_finished_ones()
    {
        var tag = $"plan-{Guid.NewGuid():N}";
        var recorder = new CommandRecorder();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(recorder))));
        var now = DateTime.UtcNow;
        await _factory.ExecuteDbAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "OutboxMessages" ("Id", "Kind", "Payload", "CreatedAt", "NextAttemptAt", "DispatchedAt", "Attempts", "LastError")
            SELECT gen_random_uuid(), 'ConversationRead', '[]'::jsonb,
                   {now.AddHours(-2)} + g * INTERVAL '1 millisecond', {now.AddHours(-2)}, {now.AddHours(-1)}, 1, {tag}
            FROM generate_series(1, 20000) AS g
            """));
        var waiting = new List<OutboxMessage>();
        for (var i = 0; i < 5; i++)
        {
            waiting.Add(await InsertAsync(
                new ConversationRead(Guid.NewGuid(), Guid.NewGuid(), now), now.AddMinutes(-1).AddMilliseconds(i), now.AddMinutes(-1)));
        }

        // Without statistics the planner knows nothing about a table that was just filled, and the plan means nothing.
        await _factory.ExecuteDbAsync(db => db.Database.ExecuteSqlRawAsync(@"ANALYZE ""OutboxMessages"""));

        try
        {
            recorder.Clear();
            await RunAsync(host);

            // The pass has to give the right answer for its plan to mean anything.
            var claimed = await _factory.QueryDbAsync(db => db.OutboxMessages
                .Where(o => waiting.Select(w => w.Id).Contains(o.Id))
                .ToListAsync());
            Assert.All(claimed, row => Assert.NotNull(row.DispatchedAt));

            // The statement is run again for its plan, so the rows it takes have to be due again.
            await _factory.ExecuteDbAsync(db => db.OutboxMessages
                .Where(o => waiting.Select(w => w.Id).Contains(o.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.DispatchedAt, (DateTime?)null).SetProperty(o => o.NextAttemptAt, now.AddMinutes(-1))));
            var claim = Assert.Single(recorder.Commands, c => c.Sql.Contains("\"OutboxMessages\""));
            var rowsRead = await _factory.QueryDbAsync(db => QueryPlan.RowsReadFromTableAsync(db, claim, "OutboxMessages"));

            // Five rows are waiting among 20,005. A walk over the whole table would read all of them.
            Assert.True(rowsRead <= 50, $"Claiming 5 waiting rows read {rowsRead} OutboxMessages rows.");
        }
        finally
        {
            await _factory.ExecuteDbAsync(db => db.OutboxMessages
                .Where(o => o.LastError == tag || waiting.Select(w => w.Id).Contains(o.Id))
                .ExecuteDeleteAsync());
        }
    }

    // The database keeps microseconds and the clock 100 nanoseconds, so a stored time is a hair off the one that was written.
    private static void AssertAbout(TimeSpan expected, TimeSpan actual) =>
        Assert.InRange(actual.TotalMilliseconds, expected.TotalMilliseconds - 1, expected.TotalMilliseconds + 1);

    private static Guid? MessageIdOf(JsonElement notification) =>
        notification.TryGetProperty("messageId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetGuid() : null;
}
