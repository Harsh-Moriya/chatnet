using System.Net;
using System.Text.Json;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Media;
using ChatNET.API.Messaging.Persistence;
using ChatNET.API.Outbox;
using ChatNET.API.Outbox.Models;
using ChatNET.API.Tests.Infrastructure;
using ChatNET.API.Tests.Media;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace ChatNET.API.Tests.Messaging;

// Deleting: the sender may delete their message at any time, and the owner or an admin of a group may delete anyone's.
// The row stays so the timeline has no gap; the text and the files go.
[Collection(IntegrationTestCollection.Name)]
public class DeleteMessageTests
{
    private readonly ChatApiFactory _factory;

    public DeleteMessageTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private static HttpClient Authorized(WebApplicationFactory<Program> host, TestUser user)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", user.Token);
        return client;
    }

    private async Task<(JsonElement Sent, Guid Conversation, TestUser Alice, TestUser Bob)> SentInDirectChatAsync(string text = "regrettable")
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        return (await client.SendMessageAsync(conversation, text), conversation, alice, bob);
    }

    // Alice owns the group; bob and carol are members. Bob and carol each get a role if asked.
    private async Task<(Guid Group, TestUser Alice, TestUser Bob, TestUser Carol)> GroupAsync(MemberRole? bobRole = null)
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var client = _factory.CreateClientFor(alice);
        var group = (await client.CreateGroupAsync("Team", bob.Id, carol.Id)).IdOf();

        if (bobRole is { } role)
        {
            await _factory.ExecuteDbAsync(db => db.ConversationMembers
                .Where(m => m.ConversationId == group && m.UserId == bob.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, role)));
        }

        return (group, alice, bob, carol);
    }

    private Task<ChatNET.API.Messaging.Models.Message> StoredAsync(JsonElement sent) =>
        _factory.QueryDbAsync(db => db.Messages.AsNoTracking().SingleAsync(m => m.Id == sent.MessageIdOf()));

    // ##### The deletion #####

    [Fact]
    public async Task The_sender_deletes_and_the_answer_is_the_deleted_message()
    {
        var (sent, conversation, alice, _) = await SentInDirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var response = await client.DeleteMessageResponseAsync(conversation, sent.MessageIdOf());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var deleted = await response.ReadJsonAsync();
        Assert.Equal(sent.MessageIdOf(), deleted.MessageIdOf());
        Assert.Equal(JsonValueKind.Null, deleted.GetProperty("content").ValueKind);
        Assert.True(deleted.GetProperty("isDeleted").GetBoolean());
        Assert.Empty(deleted.AttachmentsOf());
        Assert.Equal(sent.GetProperty("createdAt").GetDateTime(), deleted.GetProperty("createdAt").GetDateTime());
    }

    [Fact]
    public async Task The_row_is_kept_with_who_deleted_it_and_when_and_the_text_is_gone()
    {
        var (sent, conversation, alice, _) = await SentInDirectChatAsync("this will be gone");
        using var client = _factory.CreateClientFor(alice);
        var before = DateTime.UtcNow.AddSeconds(-1);

        await client.DeleteMessageAsync(conversation, sent.MessageIdOf());

        var stored = await StoredAsync(sent);
        Assert.True(stored.IsDeleted);
        Assert.Null(stored.Content);
        Assert.Equal(alice.Id, stored.DeletedById);
        Assert.InRange(stored.DeletedAt!.Value, before, DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task History_keeps_the_place_of_a_deleted_message_without_its_text()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        await aliceClient.SendMessageAsync(conversation, "one");
        var second = await aliceClient.SendMessageAsync(conversation, "two");
        await aliceClient.SendMessageAsync(conversation, "three");

        await aliceClient.DeleteMessageAsync(conversation, second.MessageIdOf());

        var history = await bobClient.GetHistoryAsync(conversation);
        Assert.Equal(3, history.Count);
        Assert.Equal<IEnumerable<string?>>(["one", null, "three"], history.Select(m => m.ContentOf()));
        Assert.True(history[1].GetProperty("isDeleted").GetBoolean());
    }

    [Fact]
    public async Task Every_tab_in_the_conversation_is_told_and_the_text_is_not_in_what_it_is_sent()
    {
        var (sent, conversation, alice, bob) = await SentInDirectChatAsync("this will be gone");
        using var client = _factory.CreateClientFor(alice);
        await using var bobTab = await HubClient.ConnectAsync(_factory, bob);
        await using var aliceOtherTab = await HubClient.ConnectAsync(_factory, alice);

        await client.DeleteMessageAsync(conversation, sent.MessageIdOf());

        foreach (var tab in new[] { bobTab, aliceOtherTab })
        {
            var pushed = await tab.WaitForUpdateAsync(sent.MessageIdOf());
            Assert.True(pushed.GetProperty("isDeleted").GetBoolean());
            Assert.Equal(JsonValueKind.Null, pushed.GetProperty("content").ValueKind);
            Assert.DoesNotContain("this will be gone", pushed.GetRawText());
        }
    }

    [Fact]
    public async Task Deleting_has_no_time_limit()
    {
        var (sent, conversation, alice, _) = await SentInDirectChatAsync();
        var clock = new ManualTimeProvider();
        using var host = _factory.WithClock(clock);
        using var client = Authorized(host, alice);
        clock.Advance(TimeSpan.FromDays(30));

        var response = await client.DeleteMessageResponseAsync(conversation, sent.MessageIdOf());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await StoredAsync(sent)).IsDeleted);
    }

    [Fact]
    public async Task Deleting_again_answers_with_the_deleted_message_and_does_nothing_else()
    {
        var (sent, conversation, alice, bob) = await SentInDirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        await client.DeleteMessageAsync(conversation, sent.MessageIdOf());
        var first = await StoredAsync(sent);
        await using var bobTab = await HubClient.ConnectAsync(_factory, bob);

        var again = await client.DeleteMessageResponseAsync(conversation, sent.MessageIdOf());

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.True((await again.ReadJsonAsync()).GetProperty("isDeleted").GetBoolean());
        Assert.Equal(first.DeletedAt, (await StoredAsync(sent)).DeletedAt);
        Assert.False(await bobTab.ReceivedUpdateAsync(sent.MessageIdOf()));
        Assert.Single(await _factory.OutboxRowsAboutAsync("messageId", sent.MessageIdOf()), r => r.Kind == MessageUpdated.Name);
    }

    // ##### Who may #####

    [Fact]
    public async Task In_a_direct_chat_only_the_sender_may_delete()
    {
        var (sent, conversation, _, bob) = await SentInDirectChatAsync();
        using var bobClient = _factory.CreateClientFor(bob);

        var response = await bobClient.DeleteMessageResponseAsync(conversation, sent.MessageIdOf());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False((await StoredAsync(sent)).IsDeleted);
    }

    [Fact]
    public async Task In_a_group_a_plain_member_cannot_delete_someone_elses_message()
    {
        var (group, alice, _, carol) = await GroupAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        using var carolClient = _factory.CreateClientFor(carol);
        var sent = await aliceClient.SendMessageAsync(group, "alice speaking");

        var response = await carolClient.DeleteMessageResponseAsync(group, sent.MessageIdOf());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False((await StoredAsync(sent)).IsDeleted);
    }

    [Fact]
    public async Task The_owner_of_a_group_may_delete_anyones_message()
    {
        var (group, alice, bob, _) = await GroupAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var sent = await bobClient.SendMessageAsync(group, "bob speaking");

        await aliceClient.DeleteMessageAsync(group, sent.MessageIdOf());

        var stored = await StoredAsync(sent);
        Assert.True(stored.IsDeleted);
        Assert.Equal(alice.Id, stored.DeletedById);
        Assert.Equal(bob.Id, stored.SenderId);
    }

    [Fact]
    public async Task An_admin_of_a_group_may_delete_anyones_message()
    {
        var (group, alice, bob, carol) = await GroupAsync(MemberRole.Admin);
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var sent = await aliceClient.SendMessageAsync(group, "owner speaking");

        await bobClient.DeleteMessageAsync(group, sent.MessageIdOf());

        Assert.Equal(bob.Id, (await StoredAsync(sent)).DeletedById);
        _ = carol;
    }

    [Fact]
    public async Task An_admin_who_left_the_group_may_not()
    {
        var (group, alice, bob, _) = await GroupAsync(MemberRole.Admin);
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var sent = await aliceClient.SendMessageAsync(group, "owner speaking");
        await _factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == group && m.UserId == bob.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LeftAt, DateTime.UtcNow)));

        var response = await bobClient.DeleteMessageResponseAsync(group, sent.MessageIdOf());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_admin_cannot_delete_what_was_said_before_they_joined_and_cannot_tell_it_exists()
    {
        var (group, alice, _, carol) = await GroupAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        var sent = await aliceClient.SendMessageAsync(group, "before dave");
        var dave = await _factory.RegisterUserAsync("dave");
        await _factory.ExecuteDbAsync(async db =>
        {
            db.ConversationMembers.Add(new ConversationMember
            {
                ConversationId = group, UserId = dave.Id, Role = MemberRole.Admin, JoinedAt = DateTime.UtcNow.AddMinutes(1),
            });
            await db.SaveChangesAsync();
        });
        using var daveClient = _factory.CreateClientFor(dave);

        var response = await daveClient.DeleteMessageResponseAsync(group, sent.MessageIdOf());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False((await StoredAsync(sent)).IsDeleted);
        _ = carol;
    }

    [Fact]
    public async Task Someone_outside_the_conversation_is_refused_and_an_unknown_message_is_not_found()
    {
        var (sent, conversation, alice, _) = await SentInDirectChatAsync();
        var outsider = await _factory.RegisterUserAsync("outsider");
        using var outsiderClient = _factory.CreateClientFor(outsider);
        using var aliceClient = _factory.CreateClientFor(alice);

        var refused = await outsiderClient.DeleteMessageResponseAsync(conversation, sent.MessageIdOf());
        var unknown = await aliceClient.DeleteMessageResponseAsync(conversation, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task A_deactivated_account_is_refused()
    {
        var (sent, conversation, alice, _) = await SentInDirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        await _factory.DeactivateAsync(alice);

        var response = await client.DeleteMessageResponseAsync(conversation, sent.MessageIdOf());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Deleting_requires_authentication()
    {
        using var client = _factory.CreateClient();

        var response = await client.DeleteMessageResponseAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ##### What deleting takes with it #####

    [Fact]
    public async Task The_files_of_a_deleted_message_are_removed_from_the_bucket_and_no_link_is_given()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var key = await client.UploadFileAsync(conversation, "hello"u8.ToArray(), "text/plain", "note.txt");
        var sent = await client.SendMessageWithFilesAsync(conversation, "see file", (key, "note.txt"));
        var file = await _factory.QueryDbAsync(db => db.MessageAttachments.SingleAsync(a => a.MessageId == sent.MessageIdOf()));
        Assert.True(await _factory.ObjectExistsAsync(file.StorageKey));

        var deleted = await client.DeleteMessageAsync(conversation, sent.MessageIdOf());

        Assert.False(await _factory.ObjectExistsAsync(file.StorageKey));
        Assert.Empty(deleted.AttachmentsOf());
        var job = Assert.Single(
            await _factory.OutboxRowsAboutAsync("messageId", sent.MessageIdOf()), r => r.Kind == MessageFilesRemoved.Name);
        Assert.NotNull(job.DispatchedAt);
        using var bobClient = _factory.CreateClientFor(bob);
        var link = await bobClient.GetAttachmentLinkResponseAsync(file.Id);
        Assert.Equal(HttpStatusCode.NotFound, link.StatusCode);
    }

    // The storage cannot be reached when the message is deleted. The deletion still succeeds, the job to remove the file
    // stays in the outbox, and the relay takes the file out once it can.
    [Fact]
    public async Task A_file_the_storage_would_not_delete_is_removed_later_by_the_relay()
    {
        var capture = new CapturingLoggerProvider();
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var key = await client.UploadFileAsync(conversation, "hello"u8.ToArray(), "text/plain", "note.txt");
        var sent = await client.SendMessageWithFilesAsync(conversation, "see file", (key, "note.txt"));
        var file = await _factory.QueryDbAsync(db => db.MessageAttachments.SingleAsync(a => a.MessageId == sent.MessageIdOf()));
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddLogging(logging => logging.AddProvider(capture));
            services.RemoveAll<IBlobStorage>();
            services.AddSingleton<IBlobStorage>(provider =>
                new UndeletableBlobStorage(ActivatorUtilities.CreateInstance<S3BlobStorage>(provider)));
        }));
        using var hostClient = Authorized(host, alice);

        var response = await hostClient.DeleteMessageResponseAsync(conversation, sent.MessageIdOf());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await StoredAsync(sent)).IsDeleted);
        Assert.True(await _factory.ObjectExistsAsync(file.StorageKey));
        var job = Assert.Single(
            await _factory.OutboxRowsAboutAsync("messageId", sent.MessageIdOf()), r => r.Kind == MessageFilesRemoved.Name);
        Assert.Null(job.DispatchedAt);
        var warning = Assert.Single(capture.Entries, e => e.StartsWith("[Warning]") && e.Contains(file.Id.ToString()));
        Assert.DoesNotContain(file.StorageKey, warning);

        var clock = new ManualTimeProvider();
        using var relayHost = _factory.WithClock(clock);
        clock.Advance(TimeSpan.FromMinutes(3));
        await relayHost.Services.GetRequiredService<OutboxRelayService>().RunOnceAsync(CancellationToken.None);

        Assert.False(await _factory.ObjectExistsAsync(file.StorageKey));
        Assert.NotNull((await _factory.OutboxRowAsync(job.Id)).DispatchedAt);
    }

    // Every file is tried even when one fails: the others go at once, and only the one that failed is left for the relay.
    [Fact]
    public async Task When_one_of_two_files_cannot_be_deleted_the_other_goes_at_once_and_the_relay_removes_the_first()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var first = await client.UploadFileAsync(conversation, "one"u8.ToArray(), "text/plain", "one.txt");
        var second = await client.UploadFileAsync(conversation, "two"u8.ToArray(), "text/plain", "two.txt");
        var sent = await client.SendMessageWithFilesAsync(conversation, "two files", (first, "one.txt"), (second, "two.txt"));
        var files = await _factory.QueryDbAsync(db => db.MessageAttachments
            .Where(a => a.MessageId == sent.MessageIdOf()).OrderBy(a => a.CreatedAt).ToListAsync());
        var stubborn = files[0].StorageKey;
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IBlobStorage>();
            services.AddSingleton<IBlobStorage>(provider => new UndeletableBlobStorage(
                ActivatorUtilities.CreateInstance<S3BlobStorage>(provider), key => key == stubborn));
        }));
        using var hostClient = Authorized(host, alice);

        await hostClient.DeleteMessageAsync(conversation, sent.MessageIdOf());

        Assert.True(await _factory.ObjectExistsAsync(files[0].StorageKey));
        Assert.False(await _factory.ObjectExistsAsync(files[1].StorageKey));

        var clock = new ManualTimeProvider();
        using var relayHost = _factory.WithClock(clock);
        clock.Advance(TimeSpan.FromMinutes(3));
        await relayHost.Services.GetRequiredService<OutboxRelayService>().RunOnceAsync(CancellationToken.None);

        Assert.False(await _factory.ObjectExistsAsync(files[0].StorageKey));
    }

    [Fact]
    public async Task A_message_without_files_has_no_job_for_files()
    {
        var (sent, conversation, alice, _) = await SentInDirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        await client.DeleteMessageAsync(conversation, sent.MessageIdOf());

        Assert.DoesNotContain(
            await _factory.OutboxRowsAboutAsync("messageId", sent.MessageIdOf()), r => r.Kind == MessageFilesRemoved.Name);
    }

    // The job is saved with the deletion, so a deletion that is not stored leaves no job to remove the files of a
    // message that still exists.
    [Fact]
    public async Task A_message_with_files_that_is_not_deleted_keeps_its_files_and_has_no_job()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var key = await client.UploadFileAsync(conversation, "hello"u8.ToArray(), "text/plain", "note.txt");
        var sent = await client.SendMessageWithFilesAsync(conversation, "see file", (key, "note.txt"));
        var file = await _factory.QueryDbAsync(db => db.MessageAttachments.SingleAsync(a => a.MessageId == sent.MessageIdOf()));
        using var scope = _factory.Services.CreateScope();
        var publisher = scope.ServiceProvider.GetRequiredService<OutboxPublisher>();
        var update = publisher.Create(new MessageUpdated(sent.MessageIdOf(), conversation));
        var removal = publisher.Create(new MessageFilesRemoved(sent.MessageIdOf()));
        removal.Kind = new string('x', 65);

        await Assert.ThrowsAsync<DbUpdateException>(() => scope.ServiceProvider.GetRequiredService<IMessageRepository>()
            .TryDeleteAsync(sent.MessageIdOf(), alice.Id, DateTime.UtcNow, [update, removal], CancellationToken.None));

        Assert.False((await StoredAsync(sent)).IsDeleted);
        Assert.True(await _factory.ObjectExistsAsync(file.StorageKey));
        Assert.Equal(0, await _factory.QueryDbAsync(db => db.OutboxMessages.CountAsync(o => o.Id == removal.Id)));
    }

    [Fact]
    public async Task A_deleted_message_is_not_unread_for_the_others_and_the_preview_says_so()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var first = await aliceClient.SendMessageAsync(conversation, "one");
        var second = await aliceClient.SendMessageAsync(conversation, "two");
        Assert.Equal(2, await bobClient.UnreadInListAsync(conversation));

        await aliceClient.DeleteMessageAsync(conversation, second.MessageIdOf());

        Assert.Equal(1, await bobClient.UnreadInListAsync(conversation));
        var last = (await bobClient.ListConversationsAsync()).Single(c => c.IdOf() == conversation).GetProperty("lastMessage");
        Assert.Equal(JsonValueKind.Null, last.GetProperty("content").ValueKind);
        _ = first;
    }

    [Fact]
    public async Task A_mention_of_a_deleted_message_stays_in_the_bell_without_the_text()
    {
        var (group, alice, bob, _) = await GroupAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var sent = await aliceClient.SendMessageAsync(group, $"secret for @{bob.UserName}");

        await aliceClient.DeleteMessageAsync(group, sent.MessageIdOf());

        var item = (await bobClient.GetNotificationsAsync()).ItemsOf()
            .Single(n => n.TryGetProperty("messageId", out var id) && id.ValueKind == JsonValueKind.String
                         && id.GetGuid() == sent.MessageIdOf());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("messagePreview").ValueKind);
    }

    // ##### Saved together, pushed reliably #####

    [Fact]
    public async Task A_push_that_fails_leaves_the_row_and_the_deletion_still_succeeds()
    {
        var (sent, conversation, alice, _) = await SentInDirectChatAsync();
        using var broken = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<Microsoft.AspNetCore.SignalR.IHubContext<ChatNET.API.Messaging.Hubs.ChatHub, ChatNET.API.Messaging.Hubs.IChatClient>>(new ThrowingHubContext());
        }));
        using var client = Authorized(broken, alice);

        var response = await client.DeleteMessageResponseAsync(conversation, sent.MessageIdOf());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = Assert.Single(
            await _factory.OutboxRowsAboutAsync("messageId", sent.MessageIdOf()), r => r.Kind == MessageUpdated.Name);
        Assert.Null(row.DispatchedAt);
        await _factory.RemoveWaitingOutboxRowsAsync();
    }

    // Two requests deleting one message: the second statement is checked against what the first wrote, changes
    // nothing, and leaves no row behind.
    [Fact]
    public async Task A_delete_that_arrives_after_another_changes_nothing_and_saves_no_row()
    {
        var (sent, conversation, alice, _) = await SentInDirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        await client.DeleteMessageAsync(conversation, sent.MessageIdOf());
        var first = await StoredAsync(sent);
        using var scope = _factory.Services.CreateScope();
        var row = scope.ServiceProvider.GetRequiredService<OutboxPublisher>()
            .Create(new MessageUpdated(sent.MessageIdOf(), conversation));

        var deleted = await scope.ServiceProvider.GetRequiredService<IMessageRepository>()
            .TryDeleteAsync(sent.MessageIdOf(), alice.Id, DateTime.UtcNow.AddMinutes(5), [row], CancellationToken.None);

        Assert.False(deleted);
        Assert.Equal(first.DeletedAt, (await StoredAsync(sent)).DeletedAt);
        Assert.Equal(0, await _factory.QueryDbAsync(db => db.OutboxMessages.CountAsync(o => o.Id == row.Id)));
    }

    // A row Postgres refuses takes the deletion down with it: a message is never deleted without its announcement.
    [Fact]
    public async Task A_message_is_not_deleted_when_its_row_cannot_be_stored()
    {
        var (sent, conversation, alice, _) = await SentInDirectChatAsync("still here");
        using var scope = _factory.Services.CreateScope();
        var row = scope.ServiceProvider.GetRequiredService<OutboxPublisher>()
            .Create(new MessageUpdated(sent.MessageIdOf(), conversation));
        row.Kind = new string('x', 65);

        await Assert.ThrowsAsync<DbUpdateException>(() => scope.ServiceProvider.GetRequiredService<IMessageRepository>()
            .TryDeleteAsync(sent.MessageIdOf(), alice.Id, DateTime.UtcNow, [row], CancellationToken.None));

        var stored = await StoredAsync(sent);
        Assert.False(stored.IsDeleted);
        Assert.Equal("still here", stored.Content);
    }

    [Fact]
    public async Task The_log_has_ids_and_never_the_text()
    {
        var capture = new CapturingLoggerProvider();
        var secret = $"secret-{Guid.NewGuid():N}";
        var (sent, conversation, alice, _) = await SentInDirectChatAsync(secret);
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddLogging(logging => logging.AddProvider(capture))));
        using var client = Authorized(host, alice);

        await client.DeleteMessageAsync(conversation, sent.MessageIdOf());

        Assert.Contains(capture.Entries, e => e.Contains("deleted by") && e.Contains(sent.MessageIdOf().ToString()));
        Assert.DoesNotContain(capture.Entries, e => e.Contains(secret));
    }
}
