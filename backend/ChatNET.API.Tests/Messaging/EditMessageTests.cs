using System.Net;
using System.Text.Json;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Messaging.EditMessage;
using ChatNET.API.Messaging.Persistence;
using ChatNET.API.Notifications.Models;
using ChatNET.API.Outbox;
using ChatNET.API.Outbox.Models;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChatNET.API.Tests.Messaging;

// Editing: the sender replaces the text of their own message for a while after sending it. Everyone in the
// conversation is sent the new version, and people the edit names for the first time are notified.
[Collection(IntegrationTestCollection.Name)]
public class EditMessageTests
{
    private readonly ChatApiFactory _factory;

    public EditMessageTests(ChatApiFactory factory)
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

    private Task<(JsonElement Sent, Guid Conversation, TestUser Alice, TestUser Bob)> SentInDirectChatAsync(string text = "first draft") =>
        SentAsync(text);

    private async Task<(JsonElement Sent, Guid Conversation, TestUser Alice, TestUser Bob)> SentAsync(string text)
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        return (await client.SendMessageAsync(conversation, text), conversation, alice, bob);
    }

    // ##### The edit #####

    [Fact]
    public async Task The_sender_replaces_the_text_and_the_answer_is_the_message_as_it_is_now()
    {
        var (sent, conversation, alice, _) = await SentInDirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var response = await client.PatchMessageAsync(conversation, sent.MessageIdOf(), "  second draft  ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var edited = await response.ReadJsonAsync();
        Assert.Equal(sent.MessageIdOf(), edited.MessageIdOf());
        Assert.Equal("second draft", edited.ContentOf());
        Assert.Equal(1, edited.GetProperty("editCount").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, edited.GetProperty("editedAt").ValueKind);
        Assert.False(edited.GetProperty("isDeleted").GetBoolean());
        Assert.Equal(sent.GetProperty("createdAt").GetDateTime(), edited.GetProperty("createdAt").GetDateTime());
        Assert.Equal(sent.GetProperty("senderDisplayName").GetString(), edited.GetProperty("senderDisplayName").GetString());
    }

    [Fact]
    public async Task History_shows_the_new_text_and_the_message_keeps_its_place()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var first = await aliceClient.SendMessageAsync(conversation, "one");
        await aliceClient.SendMessageAsync(conversation, "two");

        await aliceClient.EditMessageAsync(conversation, first.MessageIdOf(), "one, corrected");

        var history = await bobClient.GetHistoryAsync(conversation);
        Assert.Equal<IEnumerable<string?>>(["one, corrected", "two"], history.Select(m => m.ContentOf()));
        var stored = history[0];
        Assert.Equal(1, stored.GetProperty("editCount").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, stored.GetProperty("editedAt").ValueKind);
        Assert.Equal(0, history[1].GetProperty("editCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, history[1].GetProperty("editedAt").ValueKind);
    }

    [Fact]
    public async Task Every_tab_in_the_conversation_is_sent_the_new_version_the_editors_own_included()
    {
        var (sent, conversation, alice, bob) = await SentInDirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        await using var bobTab = await HubClient.ConnectAsync(_factory, bob);
        await using var aliceOtherTab = await HubClient.ConnectAsync(_factory, alice);

        await client.EditMessageAsync(conversation, sent.MessageIdOf(), "second draft");

        foreach (var tab in new[] { bobTab, aliceOtherTab })
        {
            var pushed = await tab.WaitForUpdateAsync(sent.MessageIdOf());
            Assert.Equal("second draft", pushed.ContentOf());
            Assert.Equal(1, pushed.GetProperty("editCount").GetInt32());
        }
    }

    [Fact]
    public async Task Someone_outside_the_conversation_is_sent_nothing()
    {
        var (sent, conversation, alice, _) = await SentInDirectChatAsync();
        var outsider = await _factory.RegisterUserAsync("outsider");
        using var client = _factory.CreateClientFor(alice);
        await using var outsiderTab = await HubClient.ConnectAsync(_factory, outsider);

        await client.EditMessageAsync(conversation, sent.MessageIdOf(), "second draft");

        Assert.False(await outsiderTab.ReceivedUpdateAsync(sent.MessageIdOf()));
    }

    [Fact]
    public async Task Every_edit_is_counted()
    {
        var (sent, conversation, alice, _) = await SentInDirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        await client.EditMessageAsync(conversation, sent.MessageIdOf(), "two");
        var third = await client.EditMessageAsync(conversation, sent.MessageIdOf(), "three");

        Assert.Equal(2, third.GetProperty("editCount").GetInt32());
        Assert.Equal("three", third.ContentOf());
    }

    [Fact]
    public async Task Saving_the_text_that_is_already_there_changes_nothing_and_tells_nobody()
    {
        var (sent, conversation, alice, bob) = await SentInDirectChatAsync("as it is");
        using var client = _factory.CreateClientFor(alice);
        await using var bobTab = await HubClient.ConnectAsync(_factory, bob);

        var response = await client.PatchMessageAsync(conversation, sent.MessageIdOf(), "  as it is ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answer = await response.ReadJsonAsync();
        Assert.Equal(0, answer.GetProperty("editCount").GetInt32());
        Assert.False(await bobTab.ReceivedUpdateAsync(sent.MessageIdOf()));
        Assert.DoesNotContain(
            await _factory.OutboxRowsAboutAsync("messageId", sent.MessageIdOf()), r => r.Kind == MessageUpdated.Name);
    }

    [Fact]
    public async Task An_edit_does_not_move_the_conversation_in_the_sidebar()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var first = await client.SendMessageAsync(conversation, "old news");
        var before = (await client.ListConversationsAsync()).Single(c => c.IdOf() == conversation)
            .GetProperty("updatedAt").GetDateTime();

        await client.EditMessageAsync(conversation, first.MessageIdOf(), "old news, corrected");

        var after = (await client.ListConversationsAsync()).Single(c => c.IdOf() == conversation);
        Assert.Equal(before, after.GetProperty("updatedAt").GetDateTime());
        Assert.Equal("old news, corrected", after.GetProperty("lastMessage").GetProperty("content").GetString());
    }

    // ##### Who may, and until when #####

    [Fact]
    public async Task Only_the_sender_may_edit()
    {
        var (sent, conversation, _, bob) = await SentInDirectChatAsync();
        using var bobClient = _factory.CreateClientFor(bob);

        var response = await bobClient.PatchMessageAsync(conversation, sent.MessageIdOf(), "hijacked");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("first draft", (await StoredAsync(sent)).Content);
    }

    [Fact]
    public async Task A_group_admin_may_delete_but_not_edit_other_peoples_messages()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var group = (await aliceClient.CreateGroupAsync("Team", bob.Id)).IdOf();
        var sent = await bobClient.SendMessageAsync(group, "bob's words");

        var response = await aliceClient.PatchMessageAsync(group, sent.MessageIdOf(), "alice's words");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("bob's words", (await StoredAsync(sent)).Content);
    }

    [Fact]
    public async Task Someone_outside_the_conversation_is_refused()
    {
        var (sent, conversation, _, _) = await SentInDirectChatAsync();
        var outsider = await _factory.RegisterUserAsync("outsider");
        using var client = _factory.CreateClientFor(outsider);

        var response = await client.PatchMessageAsync(conversation, sent.MessageIdOf(), "hello");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_member_who_left_is_refused()
    {
        var (sent, conversation, alice, _) = await SentInDirectChatAsync();
        await _factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == conversation && m.UserId == alice.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LeftAt, DateTime.UtcNow)));
        using var client = _factory.CreateClientFor(alice);

        var response = await client.PatchMessageAsync(conversation, sent.MessageIdOf(), "hello");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_message_that_does_not_exist_and_one_of_another_conversation_are_the_same_not_found()
    {
        var (sent, _, alice, bob) = await SentInDirectChatAsync();
        var carol = await _factory.RegisterUserAsync("carol");
        using var aliceClient = _factory.CreateClientFor(alice);
        var elsewhere = (await aliceClient.CreateDirectAsync(carol.Id)).IdOf();

        var unknown = await aliceClient.PatchMessageAsync(elsewhere, Guid.NewGuid(), "hello");
        var otherConversation = await aliceClient.PatchMessageAsync(elsewhere, sent.MessageIdOf(), "hello");

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherConversation.StatusCode);
        Assert.Equal("first draft", (await StoredAsync(sent)).Content);
        _ = bob;
    }

    [Fact]
    public async Task A_message_from_before_the_member_joined_is_not_found()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var aliceClient = _factory.CreateClientFor(alice);
        var group = (await aliceClient.CreateGroupAsync("Early", bob.Id)).IdOf();
        var sent = await aliceClient.SendMessageAsync(group, "before carol");
        await _factory.ExecuteDbAsync(async db =>
        {
            db.ConversationMembers.Add(new ConversationMember
            {
                ConversationId = group, UserId = carol.Id, Role = MemberRole.Member, JoinedAt = DateTime.UtcNow.AddMinutes(1),
            });
            await db.SaveChangesAsync();
        });
        using var carolClient = _factory.CreateClientFor(carol);

        var response = await carolClient.PatchMessageAsync(group, sent.MessageIdOf(), "hello");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_deactivated_account_is_refused()
    {
        var (sent, conversation, alice, _) = await SentInDirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        await _factory.DeactivateAsync(alice);

        var response = await client.PatchMessageAsync(conversation, sent.MessageIdOf(), "hello");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Editing_requires_authentication()
    {
        using var client = _factory.CreateClient();

        var response = await client.PatchMessageAsync(Guid.NewGuid(), Guid.NewGuid(), "hello");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_message_can_be_edited_until_the_window_has_passed_and_not_after()
    {
        var (sent, conversation, alice, _) = await SentInDirectChatAsync();
        var clock = new ManualTimeProvider();
        using var host = _factory.WithClock(clock);
        using var client = Authorized(host, alice);

        clock.Advance(TimeSpan.FromMinutes(14));
        var inTime = await client.PatchMessageAsync(conversation, sent.MessageIdOf(), "still in time");

        clock.Advance(TimeSpan.FromMinutes(2));
        var late = await client.PatchMessageAsync(conversation, sent.MessageIdOf(), "too late");

        Assert.Equal(HttpStatusCode.OK, inTime.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal(EditMessageHandler.TooLate, (await late.ReadJsonAsync()).GetProperty("detail").GetString());
        Assert.Equal("still in time", (await StoredAsync(sent)).Content);
    }

    [Fact]
    public async Task The_window_is_a_setting()
    {
        var (sent, conversation, alice, _) = await SentInDirectChatAsync();
        var clock = new ManualTimeProvider();
        using var host = _factory.WithClock(clock).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Configure<ChatNET.API.Messaging.MessagingOptions>(o => o.EditWindow = TimeSpan.FromMinutes(1))));
        using var client = Authorized(host, alice);

        clock.Advance(TimeSpan.FromMinutes(2));
        var response = await client.PatchMessageAsync(conversation, sent.MessageIdOf(), "too late");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_deleted_message_cannot_be_edited()
    {
        var (sent, conversation, alice, _) = await SentInDirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        await client.DeleteMessageAsync(conversation, sent.MessageIdOf());

        var response = await client.PatchMessageAsync(conversation, sent.MessageIdOf(), "back from the dead");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(EditMessageHandler.Deleted, (await response.ReadJsonAsync()).GetProperty("detail").GetString());
        Assert.Null((await StoredAsync(sent)).Content);
    }

    // ##### The new text #####

    [Fact]
    public async Task The_new_text_follows_the_rules_of_sending()
    {
        var (sent, conversation, alice, _) = await SentInDirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var blank = await client.PatchMessageAsync(conversation, sent.MessageIdOf(), "   ");
        var tooLong = await client.PatchMessageAsync(conversation, sent.MessageIdOf(), new string('x', 4001));
        var missing = await client.PatchMessageAsync(conversation, sent.MessageIdOf(), null);
        var longest = await client.PatchMessageAsync(conversation, sent.MessageIdOf(), new string('y', 4000));

        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Contains(EditMessageHandler.NothingToSay, await blank.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.OK, longest.StatusCode);
    }

    [Fact]
    public async Task A_message_with_files_may_lose_all_its_text_and_keeps_its_files()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        await using var bobTab = await HubClient.ConnectAsync(_factory, bob);
        using var client = _factory.CreateClientFor(alice);
        var key = await client.UploadFileAsync(conversation, "hello"u8.ToArray(), "text/plain", "note.txt");
        var sent = await client.SendMessageWithFilesAsync(conversation, "see file", (key, "note.txt"));

        var edited = await client.EditMessageAsync(conversation, sent.MessageIdOf(), "   ");

        Assert.Equal("", edited.ContentOf());
        var file = Assert.Single(edited.AttachmentsOf());
        Assert.Equal("note.txt", file.GetProperty("fileName").GetString());
        var pushed = await bobTab.WaitForUpdateAsync(sent.MessageIdOf());
        Assert.Single(pushed.AttachmentsOf());
    }

    // ##### Mentions #####

    [Fact]
    public async Task An_edit_notifies_only_the_people_it_names_for_the_first_time()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var client = _factory.CreateClientFor(alice);
        var group = (await client.CreateGroupAsync("Mentions", bob.Id, carol.Id)).IdOf();
        var sent = await client.SendMessageAsync(group, $"hello @{bob.UserName}");
        await using var bobTab = await HubClient.ConnectAsync(_factory, bob);
        await using var carolTab = await HubClient.ConnectAsync(_factory, carol);

        await client.EditMessageAsync(group, sent.MessageIdOf(), $"hello @{bob.UserName} and @{carol.UserName}");

        var notification = await carolTab.WaitForNotificationAsync(n => MessageIdOf(n) == sent.MessageIdOf());
        Assert.Equal("Mention", notification.GetProperty("type").GetString());
        Assert.False(await bobTab.ReceivedNotificationAsync(n => MessageIdOf(n) == sent.MessageIdOf()));
        var mentions = await _factory.QueryDbAsync(db => db.Notifications
            .Where(n => n.MessageId == sent.MessageIdOf() && n.Type == NotificationType.Mention)
            .Select(n => n.UserId).ToListAsync());
        Assert.Equal(new[] { bob.Id, carol.Id }.Order().ToArray(), mentions.Order().ToArray());
    }

    [Fact]
    public async Task Taking_a_name_out_does_not_take_its_notification_back()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);
        var group = (await client.CreateGroupAsync("Mentions", bob.Id)).IdOf();
        var sent = await client.SendMessageAsync(group, $"hello @{bob.UserName}");

        await client.EditMessageAsync(group, sent.MessageIdOf(), "hello nobody");

        Assert.Equal(1, await _factory.QueryDbAsync(db => db.Notifications
            .CountAsync(n => n.MessageId == sent.MessageIdOf() && n.UserId == bob.Id)));
    }

    [Fact]
    public async Task Naming_yourself_or_someone_outside_the_group_notifies_nobody()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var outsider = await _factory.RegisterUserAsync("outsider");
        using var client = _factory.CreateClientFor(alice);
        var group = (await client.CreateGroupAsync("Mentions", bob.Id)).IdOf();
        var sent = await client.SendMessageAsync(group, "no names yet");

        await client.EditMessageAsync(group, sent.MessageIdOf(), $"@{alice.UserName} @{outsider.UserName}");

        Assert.Equal(0, await _factory.QueryDbAsync(db => db.Notifications.CountAsync(n => n.MessageId == sent.MessageIdOf())));
    }

    // ##### Saved together, pushed reliably #####

    [Fact]
    public async Task The_edit_and_its_row_are_saved_together_and_the_row_is_marked_done()
    {
        var (sent, conversation, alice, _) = await SentInDirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        await client.EditMessageAsync(conversation, sent.MessageIdOf(), "second draft");

        var row = Assert.Single(
            await _factory.OutboxRowsAboutAsync("messageId", sent.MessageIdOf()), r => r.Kind == MessageUpdated.Name);
        Assert.NotNull(row.DispatchedAt);
    }

    // A refused row takes the whole edit down: the text, the count and the notifications.
    [Fact]
    public async Task An_edit_is_not_stored_when_its_row_cannot_be_stored()
    {
        var (sent, conversation, alice, _) = await SentInDirectChatAsync();
        using var scope = _factory.Services.CreateScope();
        var publisher = scope.ServiceProvider.GetRequiredService<OutboxPublisher>();
        var row = publisher.Create(new MessageUpdated(sent.MessageIdOf(), conversation));
        row.Kind = new string('x', 65);
        var repository = scope.ServiceProvider.GetRequiredService<IMessageRepository>();

        await Assert.ThrowsAsync<DbUpdateException>(() => repository.TryEditAsync(
            sent.MessageIdOf(), "second draft", DateTime.UtcNow, DateTime.UtcNow.AddHours(-1), [], [row], CancellationToken.None));

        var stored = await StoredAsync(sent);
        Assert.Equal("first draft", stored.Content);
        Assert.Equal(0, stored.EditCount);
        Assert.Null(stored.EditedAt);
    }

    // The repository settles a race between an edit and a delete: whichever statement runs second is checked against
    // what the first wrote. A delete that came first wins, and the edit leaves nothing behind.
    [Fact]
    public async Task An_edit_that_arrives_after_a_delete_changes_nothing_and_saves_nothing()
    {
        var (sent, conversation, alice, bob) = await SentInDirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        await client.DeleteMessageAsync(conversation, sent.MessageIdOf());
        using var scope = _factory.Services.CreateScope();
        var publisher = scope.ServiceProvider.GetRequiredService<OutboxPublisher>();
        var row = publisher.Create(new MessageUpdated(sent.MessageIdOf(), conversation));
        var notification = new Notification
        {
            Id = Guid.CreateVersion7(), UserId = bob.Id, ActorId = alice.Id, ConversationId = conversation,
            MessageId = sent.MessageIdOf(), Type = NotificationType.Mention, CreatedAt = DateTime.UtcNow,
        };

        var edited = await scope.ServiceProvider.GetRequiredService<IMessageRepository>().TryEditAsync(
            sent.MessageIdOf(), "too late", DateTime.UtcNow, DateTime.UtcNow.AddHours(-1), [notification], [row],
            CancellationToken.None);

        Assert.False(edited);
        Assert.Null((await StoredAsync(sent)).Content);
        Assert.Equal(0, await _factory.QueryDbAsync(db => db.OutboxMessages.CountAsync(o => o.Id == row.Id)));
        Assert.Equal(0, await _factory.QueryDbAsync(db => db.Notifications.CountAsync(n => n.Id == notification.Id)));
    }

    [Fact]
    public async Task An_edit_at_the_end_of_the_window_is_refused_by_the_statement_itself()
    {
        var (sent, conversation, _, _) = await SentInDirectChatAsync();
        using var scope = _factory.Services.CreateScope();
        var row = scope.ServiceProvider.GetRequiredService<OutboxPublisher>().Create(new MessageUpdated(sent.MessageIdOf(), conversation));

        var edited = await scope.ServiceProvider.GetRequiredService<IMessageRepository>().TryEditAsync(
            sent.MessageIdOf(), "too late", DateTime.UtcNow, DateTime.UtcNow.AddHours(1), [], [row], CancellationToken.None);

        Assert.False(edited);
        Assert.Equal(0, (await StoredAsync(sent)).EditCount);
    }

    [Fact]
    public async Task A_push_that_fails_leaves_the_row_and_the_edit_still_succeeds()
    {
        var (sent, conversation, alice, _) = await SentInDirectChatAsync();
        using var broken = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<Microsoft.AspNetCore.SignalR.IHubContext<ChatNET.API.Messaging.Hubs.ChatHub, ChatNET.API.Messaging.Hubs.IChatClient>>(new ThrowingHubContext());
        }));
        using var client = Authorized(broken, alice);

        var response = await client.PatchMessageAsync(conversation, sent.MessageIdOf(), "second draft");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = Assert.Single(
            await _factory.OutboxRowsAboutAsync("messageId", sent.MessageIdOf()), r => r.Kind == MessageUpdated.Name);
        Assert.Null(row.DispatchedAt);
        await _factory.RemoveWaitingOutboxRowsAsync();
    }

    [Fact]
    public async Task The_log_has_ids_and_never_the_text()
    {
        var capture = new CapturingLoggerProvider();
        var (sent, conversation, alice, _) = await SentInDirectChatAsync();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddLogging(logging => logging.AddProvider(capture))));
        using var client = Authorized(host, alice);
        var secret = $"secret-{Guid.NewGuid():N}";

        await client.EditMessageAsync(conversation, sent.MessageIdOf(), secret);

        Assert.Contains(capture.Entries, e => e.Contains("edited by") && e.Contains(sent.MessageIdOf().ToString()));
        Assert.DoesNotContain(capture.Entries, e => e.Contains(secret));
    }

    // ##### Helpers #####

    private Task<ChatNET.API.Messaging.Models.Message> StoredAsync(JsonElement sent) =>
        _factory.QueryDbAsync(db => db.Messages.AsNoTracking().SingleAsync(m => m.Id == sent.MessageIdOf()));

    private static Guid? MessageIdOf(JsonElement notification) =>
        notification.TryGetProperty("messageId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetGuid() : null;
}
