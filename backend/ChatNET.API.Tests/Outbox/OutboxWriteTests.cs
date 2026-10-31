using System.Net;
using System.Text.Json;
using ChatNET.API.Common.Persistence;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Messaging.Models;
using ChatNET.API.Messaging.Persistence;
using ChatNET.API.Notifications.Models;
using ChatNET.API.Outbox;
using ChatNET.API.Outbox.Models;
using ChatNET.API.Tests.Infrastructure;
using ChatNET.API.Tests.Messaging;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ChatNET.API.Tests.Outbox;

// A push that follows a save is written to the outbox in the same transaction as the save: a stored change always has
// its row, and a change that was not stored has none. The request pushes right after the commit and marks the row
// done; the relay (OutboxRelayTests) handles the rows that were not.
[Collection(IntegrationTestCollection.Name)]
public class OutboxWriteTests
{
    private readonly ChatApiFactory _factory;

    public OutboxWriteTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private static T EventOf<T>(OutboxMessage row) where T : IOutboxEvent =>
        Assert.IsType<T>(OutboxEvents.Read(row.Kind, row.Payload));

    // ##### What is written, and when it counts as done #####

    [Fact]
    public async Task A_sent_message_has_a_row_that_is_marked_done_once_the_push_was_made()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var sent = await client.SendMessageAsync(conversation, "hello");

        var row = Assert.Single(await _factory.OutboxRowsAboutAsync("messageId", sent.MessageIdOf()));
        Assert.Equal(MessageCreated.Name, row.Kind);
        var created = EventOf<MessageCreated>(row);
        Assert.Equal(conversation, created.ConversationId);
        Assert.NotNull(row.DispatchedAt);
        Assert.Equal(0, row.Attempts);
    }

    [Fact]
    public async Task A_push_that_failed_leaves_its_row_waiting_and_the_send_still_succeeds()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var brokenHub = BrokenHub();
        using var client = Authorized(brokenHub, alice);

        var response = await client.PostMessageAsync(conversation, "hello");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var sent = await response.ReadJsonAsync();
        var row = Assert.Single(await _factory.OutboxRowsAboutAsync("messageId", sent.MessageIdOf()));
        Assert.Null(row.DispatchedAt);
        Assert.Equal(0, row.Attempts);
        await _factory.RemoveWaitingOutboxRowsAsync();
    }

    [Fact]
    public async Task A_new_row_is_left_to_its_request_for_the_configured_time()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var before = DateTime.UtcNow;

        var sent = await client.SendMessageAsync(conversation, "hello");

        var row = Assert.Single(await _factory.OutboxRowsAboutAsync("messageId", sent.MessageIdOf()));
        Assert.Equal(TimeSpan.FromMinutes(2), row.NextAttemptAt - row.CreatedAt);
        Assert.InRange(row.CreatedAt, before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task A_mention_has_a_row_for_its_notification()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);
        var group = (await client.CreateGroupAsync("Mentions", bob.Id)).IdOf();

        var sent = await client.SendMessageAsync(group, $"look, @{bob.UserName}");

        var notification = await _factory.QueryDbAsync(db => db.Notifications
            .SingleAsync(n => n.MessageId == sent.MessageIdOf()));
        var row = Assert.Single(await _factory.OutboxRowsAboutAsync("notificationId", notification.Id));
        var created = EventOf<NotificationCreated>(row);
        Assert.Equal(bob.Id, created.UserId);
        Assert.NotNull(row.DispatchedAt);
    }

    [Fact]
    public async Task A_group_has_a_row_for_its_announcement_and_one_for_each_invitation()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        var carol = await _factory.RegisterUserAsync("carol");
        using var client = _factory.CreateClientFor(alice);

        var group = (await client.CreateGroupAsync("Team", bob.Id, carol.Id)).IdOf();

        var announcement = Assert.Single(
            await _factory.OutboxRowsAboutAsync("conversationId", group), r => r.Kind == ConversationCreated.Name);
        var created = EventOf<ConversationCreated>(announcement);
        Assert.Equal(alice.Id, created.CreatorId);
        Assert.Equal(
            new[] { alice.Id, bob.Id, carol.Id }.Order().ToArray(),
            created.MemberIds.Order().ToArray());
        Assert.NotNull(announcement.DispatchedAt);

        var invitations = await _factory.QueryDbAsync(db => db.Notifications
            .Where(n => n.ConversationId == group && n.Type == NotificationType.MemberAdded)
            .ToListAsync());
        Assert.Equal(2, invitations.Count);
        foreach (var invitation in invitations)
        {
            var row = Assert.Single(await _factory.OutboxRowsAboutAsync("notificationId", invitation.Id));
            Assert.Equal(invitation.UserId, EventOf<NotificationCreated>(row).UserId);
            Assert.NotNull(row.DispatchedAt);
        }
    }

    [Fact]
    public async Task A_new_direct_conversation_has_a_row_and_asking_again_adds_none()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);

        var conversation = (await client.CreateDirectAsync(bob.Id)).IdOf();
        await client.CreateDirectAsync(bob.Id);

        var row = Assert.Single(await _factory.OutboxRowsAboutAsync("conversationId", conversation));
        Assert.Equal(ConversationCreated.Name, row.Kind);
        Assert.NotNull(row.DispatchedAt);
    }

    [Fact]
    public async Task A_read_that_moved_has_a_row_and_one_that_did_not_has_none()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var first = await bobClient.SendMessageAsync(conversation, "one");
        var second = await bobClient.SendMessageAsync(conversation, "two");

        await aliceClient.MarkReadAsync(conversation, second.MessageIdOf());
        await aliceClient.MarkReadAsync(conversation, second.MessageIdOf());
        await aliceClient.MarkReadAsync(conversation, first.MessageIdOf());

        var reads = (await _factory.OutboxRowsAboutAsync("conversationId", conversation))
            .Where(r => r.Kind == ConversationRead.Name)
            .Select(EventOf<ConversationRead>)
            .Where(e => e.UserId == alice.Id)
            .ToList();
        var read = Assert.Single(reads);
        Assert.Equal(second.GetProperty("createdAt").GetDateTime(), read.ReadAt);
    }

    // ##### All or nothing #####

    // The row and what it announces share one SaveChanges. A row Postgres refuses (a kind longer than the column)
    // takes the message down with it, so there is never a message nobody was told about.
    [Fact]
    public async Task A_message_is_not_stored_when_its_row_cannot_be_stored()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var scope = _factory.Services.CreateScope();
        var publisher = scope.ServiceProvider.GetRequiredService<OutboxPublisher>();
        var repository = new MessageRepository(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        var message = new Message
        {
            Id = Guid.CreateVersion7(),
            ConversationId = conversation,
            SenderId = alice.Id,
            Content = "atomic",
            CreatedAt = DateTime.UtcNow,
        };
        var good = publisher.Create(new MessageCreated(message.Id, conversation));
        var bad = publisher.Create(new MessageCreated(message.Id, conversation));
        bad.Kind = new string('x', 65);

        await Assert.ThrowsAsync<DbUpdateException>(() => repository.TryAddAsync(message, [], [good, bad], CancellationToken.None));

        Assert.Equal(0, await _factory.QueryDbAsync(db => db.Messages.CountAsync(m => m.Id == message.Id)));
        Assert.Equal(0, await _factory.QueryDbAsync(db => db.OutboxMessages.CountAsync(o => o.Id == good.Id)));
    }

    // The position moves and its row is stored in one transaction: a row Postgres refuses takes the move down with it, so
    // the position never moves without the announcement.
    [Fact]
    public async Task A_read_position_does_not_move_when_its_row_cannot_be_stored()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        using var bobClient = _factory.CreateClientFor(bob);
        var sent = await bobClient.SendMessageAsync(conversation, "hi");
        var readAt = sent.GetProperty("createdAt").GetDateTime();
        using var scope = _factory.Services.CreateScope();
        var row = scope.ServiceProvider.GetRequiredService<OutboxPublisher>()
            .Create(new ConversationRead(conversation, alice.Id, readAt));
        row.Kind = new string('x', 65);

        await Assert.ThrowsAsync<DbUpdateException>(() => scope.ServiceProvider.GetRequiredService<IConversationRepository>()
            .AdvanceLastReadAsync(conversation, alice.Id, readAt, row, CancellationToken.None));

        Assert.Null(await _factory.LastReadAtAsync(conversation, alice.Id));
    }

    // Two copies of one send: the unique index lets one in, and the other's rows are not saved either.
    [Fact]
    public async Task A_send_that_loses_to_a_copy_of_itself_leaves_no_row()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        var clientMessageId = Guid.NewGuid();
        using var firstClient = _factory.CreateClientFor(alice);
        var winner = await firstClient.SendMessageAsync(conversation, "once", clientMessageId);
        using var forgetful = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMessageRepository>();
            services.AddScoped<IMessageRepository>(provider =>
                new ForgetfulMessageRepository(ActivatorUtilities.CreateInstance<MessageRepository>(provider)));
        }));
        using var client = Authorized(forgetful, alice);

        var loser = await client.PostMessageAsync(conversation, "once", clientMessageId);

        Assert.Equal(HttpStatusCode.OK, loser.StatusCode);
        var rows = (await _factory.OutboxRowsAboutAsync("conversationId", conversation))
            .Where(r => r.Kind == MessageCreated.Name)
            .ToList();
        var only = Assert.Single(rows);
        Assert.Equal(winner.MessageIdOf(), EventOf<MessageCreated>(only).MessageId);
    }

    // The insert that loses the race for a direct conversation takes its announcement down with it.
    [Fact]
    public async Task A_direct_conversation_that_loses_a_race_leaves_no_row()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var winnerScope = _factory.Services.CreateScope();
        using var loserScope = _factory.Services.CreateScope();
        var winner = new ConversationRepository(winnerScope.ServiceProvider.GetRequiredService<AppDbContext>());
        var loser = new ConversationRepository(loserScope.ServiceProvider.GetRequiredService<AppDbContext>());
        var publisher = winnerScope.ServiceProvider.GetRequiredService<OutboxPublisher>();
        var winnerConversation = NewDirect(alice, bob);
        var loserConversation = NewDirect(bob, alice);
        var winnerRow = publisher.Create(new ConversationCreated(winnerConversation.Id, alice.Id, [alice.Id, bob.Id]));
        var loserRow = publisher.Create(new ConversationCreated(loserConversation.Id, bob.Id, [alice.Id, bob.Id]));

        Assert.True(await winner.TryAddDirectAsync(winnerConversation, [winnerRow], CancellationToken.None));
        Assert.False(await loser.TryAddDirectAsync(loserConversation, [loserRow], CancellationToken.None));

        Assert.Equal(1, await _factory.QueryDbAsync(db => db.OutboxMessages.CountAsync(o => o.Id == winnerRow.Id)));
        Assert.Equal(0, await _factory.QueryDbAsync(db => db.OutboxMessages.CountAsync(o => o.Id == loserRow.Id)));
    }

    // A refused read moves nothing and writes nothing.
    [Fact]
    public async Task A_read_that_moves_nothing_writes_no_row_even_when_asked_directly()
    {
        var (alice, bob, conversation) = await _factory.DirectChatAsync();
        using var bobClient = _factory.CreateClientFor(bob);
        var sent = await bobClient.SendMessageAsync(conversation, "hi");
        var readAt = sent.GetProperty("createdAt").GetDateTime();
        await _factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == conversation && m.UserId == alice.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastReadAt, readAt)));
        using var scope = _factory.Services.CreateScope();
        var row = scope.ServiceProvider.GetRequiredService<OutboxPublisher>()
            .Create(new ConversationRead(conversation, alice.Id, readAt));

        var moved = await scope.ServiceProvider.GetRequiredService<IConversationRepository>()
            .AdvanceLastReadAsync(conversation, alice.Id, readAt, row, CancellationToken.None);

        Assert.False(moved);
        Assert.Equal(0, await _factory.QueryDbAsync(db => db.OutboxMessages.CountAsync(o => o.Id == row.Id)));
    }

    // ##### Not stored in the row #####

    [Fact]
    public async Task A_row_holds_ids_and_never_a_download_link_or_the_text()
    {
        var (alice, _, conversation) = await _factory.DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var text = $"private-{Guid.NewGuid():N}";

        var key = await client.UploadFileAsync(conversation, "hello"u8.ToArray(), "text/plain", "note.txt");

        var sent = await client.SendMessageWithFilesAsync(conversation, text, (key, "note.txt"));

        var row = Assert.Single(await _factory.OutboxRowsAboutAsync("messageId", sent.MessageIdOf()));
        Assert.DoesNotContain("http", row.Payload);
        Assert.DoesNotContain("X-Amz", row.Payload);
        Assert.DoesNotContain(text, row.Payload);
        using var payload = JsonDocument.Parse(row.Payload);
        Assert.Equal(
            new[] { "conversationId", "messageId" },
            payload.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
    }

    // ##### Helpers #####

    private WebApplicationFactory<Program> BrokenHub() =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<Microsoft.AspNetCore.SignalR.IHubContext<ChatNET.API.Messaging.Hubs.ChatHub, ChatNET.API.Messaging.Hubs.IChatClient>>();
            services.AddSingleton<Microsoft.AspNetCore.SignalR.IHubContext<ChatNET.API.Messaging.Hubs.ChatHub, ChatNET.API.Messaging.Hubs.IChatClient>>(new ThrowingHubContext());
        }));

    private static HttpClient Authorized(WebApplicationFactory<Program> host, TestUser user)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", user.Token);
        return client;
    }

    private static Conversation NewDirect(TestUser first, TestUser second) => new()
    {
        Id = Guid.CreateVersion7(),
        Type = ConversationType.Direct,
        DirectKey = DirectKey.For(first.Id, second.Id),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
        CreatedById = first.Id,
        Members =
        [
            new ConversationMember { UserId = first.Id, Role = MemberRole.Member, JoinedAt = DateTime.UtcNow },
            new ConversationMember { UserId = second.Id, Role = MemberRole.Member, JoinedAt = DateTime.UtcNow },
        ],
    };
}
