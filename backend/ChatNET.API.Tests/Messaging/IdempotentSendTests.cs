using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChatNET.API.Messaging.Persistence;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ChatNET.API.Tests.Messaging;

// A send that reaches the server twice must be one message. The usual cause is a timeout: the first request
// was stored, its answer was lost, and the client (which cannot know) sends it again. The client's own
// message id, generated before the first attempt, is what lets the server see the second as a repeat.
[Collection(IntegrationTestCollection.Name)]
public class IdempotentSendTests
{
    private readonly ChatApiFactory _factory;

    public IdempotentSendTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    // ##### A retry #####

    [Fact]
    public async Task Sending_the_same_message_again_returns_the_stored_one_with_200_and_stores_nothing_new()
    {
        var (alice, _, conversation) = await DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var clientMessageId = Guid.NewGuid();

        var first = await client.PostMessageAsync(conversation, "Once", clientMessageId);
        var second = await client.PostMessageAsync(conversation, "Once", clientMessageId);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var created = await first.Content.ReadFromJsonAsync<JsonElement>();
        var repeated = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(created.GetRawText(), repeated.GetRawText());
        Assert.Equal(1, await CountAsync(conversation, alice.Id));
    }

    // The first send wins. A retry is the same send, so whatever text it carries is not a second message and
    // does not change the first.
    [Fact]
    public async Task The_stored_message_wins_if_the_retry_carries_other_text()
    {
        var (alice, _, conversation) = await DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var clientMessageId = Guid.NewGuid();
        await client.SendMessageAsync(conversation, "What I meant", clientMessageId);

        var second = await client.PostMessageAsync(conversation, "Something else", clientMessageId);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("What I meant", (await second.Content.ReadFromJsonAsync<JsonElement>()).ContentOf());
        Assert.Equal(1, await CountAsync(conversation, alice.Id));
    }

    [Fact]
    public async Task A_retry_does_not_move_the_conversation_or_the_senders_read_position_again()
    {
        var (alice, _, conversation) = await DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var clientMessageId = Guid.NewGuid();
        await client.SendMessageAsync(conversation, "Once", clientMessageId);
        var before = await _factory.QueryDbAsync(db => db.Conversations.SingleAsync(c => c.Id == conversation));
        await Task.Delay(20);

        await client.PostMessageAsync(conversation, "Once", clientMessageId);

        var after = await _factory.QueryDbAsync(db => db.Conversations.SingleAsync(c => c.Id == conversation));
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    // A repeat is answered from the lookup. Without it the retry would still end well (the index would refuse
    // the insert) but through a failed database command each time, which is noise in the log and a wasted
    // transaction.
    [Fact]
    public async Task A_repeat_is_answered_from_the_lookup_without_a_failed_insert()
    {
        var (alice, _, conversation) = await DirectChatAsync();
        var capture = new CapturingLoggerProvider();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddLogging(logging => logging.AddProvider(capture))));
        using var client = Authorized(host, alice);
        var clientMessageId = Guid.NewGuid();
        await client.SendMessageAsync(conversation, "Once", clientMessageId);

        var repeat = await client.PostMessageAsync(conversation, "Once", clientMessageId);

        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.DoesNotContain(capture.Entries, e => e.Contains("Failed executing DbCommand"));
    }

    [Fact]
    public async Task The_history_returns_the_client_id_the_message_was_sent_with()
    {
        var (alice, _, conversation) = await DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var clientMessageId = Guid.NewGuid();
        await client.SendMessageAsync(conversation, "Round trip", clientMessageId);

        var loaded = Assert.Single(await client.GetHistoryAsync(conversation));

        Assert.Equal(clientMessageId, loaded.GetProperty("clientMessageId").GetGuid());
    }

    [Fact]
    public async Task A_retry_of_a_message_that_was_deleted_since_returns_it_without_its_content()
    {
        var (alice, _, conversation) = await DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var clientMessageId = Guid.NewGuid();
        var sent = await client.SendMessageAsync(conversation, "Regretted", clientMessageId);
        await _factory.ExecuteDbAsync(db => db.Messages
            .Where(m => m.Id == sent.MessageIdOf())
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsDeleted, true).SetProperty(m => m.Content, (string?)null)));

        var retry = await client.PostMessageAsync(conversation, "Regretted", clientMessageId);

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var body = await retry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(sent.MessageIdOf(), body.MessageIdOf());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("content").ValueKind);
    }

    // ##### What is not a retry #####

    [Fact]
    public async Task Messages_sent_without_a_client_id_are_never_merged()
    {
        var (alice, _, conversation) = await DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);

        var first = await client.PostMessageAsync(conversation, "Same text");
        var second = await client.PostMessageAsync(conversation, "Same text");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(2, await CountAsync(conversation, alice.Id));
    }

    [Fact]
    public async Task The_same_client_id_from_another_sender_is_another_message()
    {
        var (alice, bob, conversation) = await DirectChatAsync();
        using var aliceClient = _factory.CreateClientFor(alice);
        using var bobClient = _factory.CreateClientFor(bob);
        var clientMessageId = Guid.NewGuid();

        var fromAlice = await aliceClient.PostMessageAsync(conversation, "Hi", clientMessageId);
        var fromBob = await bobClient.PostMessageAsync(conversation, "Hi", clientMessageId);

        Assert.Equal(HttpStatusCode.Created, fromAlice.StatusCode);
        Assert.Equal(HttpStatusCode.Created, fromBob.StatusCode);
        Assert.Equal(2, await _factory.QueryDbAsync(db => db.Messages.CountAsync(m => m.ConversationId == conversation)));
    }

    [Fact]
    public async Task The_same_client_id_in_another_conversation_is_another_message()
    {
        var (alice, _, first) = await DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var carol = await _factory.RegisterUserAsync("carol");
        var second = (await client.CreateDirectAsync(carol.Id)).IdOf();
        var clientMessageId = Guid.NewGuid();

        var inFirst = await client.PostMessageAsync(first, "Hi", clientMessageId);
        var inSecond = await client.PostMessageAsync(second, "Hi", clientMessageId);

        Assert.Equal(HttpStatusCode.Created, inFirst.StatusCode);
        Assert.Equal(HttpStatusCode.Created, inSecond.StatusCode);
        Assert.Equal(2, await _factory.QueryDbAsync(db => db.Messages.CountAsync(m => m.ClientMessageId == clientMessageId)));
    }

    // ##### Who may retry #####

    [Fact]
    public async Task Someone_who_has_left_cannot_get_their_message_back_by_retrying()
    {
        var (alice, _, conversation) = await DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var clientMessageId = Guid.NewGuid();
        await client.SendMessageAsync(conversation, "Before I left", clientMessageId);
        await _factory.ExecuteDbAsync(db => db.ConversationMembers
            .Where(m => m.ConversationId == conversation && m.UserId == alice.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LeftAt, DateTime.UtcNow)));

        var retry = await client.PostMessageAsync(conversation, "Before I left", clientMessageId);

        Assert.Equal(HttpStatusCode.Forbidden, retry.StatusCode);
    }

    [Fact]
    public async Task A_deactivated_sender_is_refused_on_a_retry_too()
    {
        var (alice, _, conversation) = await DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        var clientMessageId = Guid.NewGuid();
        await client.SendMessageAsync(conversation, "Before", clientMessageId);
        await _factory.ExecuteDbAsync(db => db.Users
            .Where(u => u.Id == alice.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsDeactivated, true)));

        var retry = await client.PostMessageAsync(conversation, "Before", clientMessageId);

        Assert.Equal(HttpStatusCode.Forbidden, retry.StatusCode);
    }

    // ##### Copies that arrive together #####

    // The lookup in the handler is only a shortcut. When two copies both miss it, the unique index decides,
    // and the loser must answer with the winner's message instead of failing.
    [Fact]
    public async Task A_copy_that_loses_the_race_to_the_index_answers_with_the_winners_message()
    {
        var (alice, _, conversation) = await DirectChatAsync();
        using var firstClient = _factory.CreateClientFor(alice);
        var clientMessageId = Guid.NewGuid();
        var winner = await firstClient.SendMessageAsync(conversation, "Winner", clientMessageId);
        var capture = new CapturingLoggerProvider();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddLogging(logging => logging.AddProvider(capture));
            services.RemoveAll<IMessageRepository>();
            services.AddScoped<IMessageRepository>(provider =>
                new ForgetfulMessageRepository(ActivatorUtilities.CreateInstance<MessageRepository>(provider)));
        }));
        using var client = Authorized(host, alice);

        var loser = await client.PostMessageAsync(conversation, "Winner", clientMessageId);

        Assert.Equal(HttpStatusCode.OK, loser.StatusCode);
        Assert.Equal(winner.MessageIdOf(), (await loser.Content.ReadFromJsonAsync<JsonElement>()).MessageIdOf());
        Assert.Equal(1, await CountAsync(conversation, alice.Id));
        Assert.DoesNotContain(capture.Entries, e => e.StartsWith("[Error]"));
    }

    [Fact]
    public async Task Many_copies_of_one_send_arriving_together_store_one_message()
    {
        var (alice, _, conversation) = await DirectChatAsync();
        var capture = new CapturingLoggerProvider();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddLogging(logging => logging.AddProvider(capture))));
        using var client = Authorized(host, alice);
        var clientMessageId = Guid.NewGuid();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => client.PostMessageAsync(conversation, "Together", clientMessageId)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(9, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        var ids = new HashSet<Guid>();
        foreach (var response in responses)
            ids.Add((await response.Content.ReadFromJsonAsync<JsonElement>()).MessageIdOf());
        Assert.Single(ids);
        Assert.Equal(1, await CountAsync(conversation, alice.Id));
        Assert.DoesNotContain(capture.Entries, e => e.StartsWith("[Error]"));
    }

    // ##### Delivery #####

    // The push after a send is best effort, and a retry is the one moment the server learns the first attempt
    // may have failed at that step. Pushing again repairs it; clients merge the copy by id.
    [Fact]
    public async Task A_retry_pushes_the_message_again_to_the_conversation()
    {
        var (alice, bob, conversation) = await DirectChatAsync();
        using var client = _factory.CreateClientFor(alice);
        await using var bobsTab = await HubClient.ConnectAsync(_factory, bob);
        var clientMessageId = Guid.NewGuid();

        var sent = await client.SendMessageAsync(conversation, "Twice pushed", clientMessageId);
        await client.PostMessageAsync(conversation, "Twice pushed", clientMessageId);

        await WaitUntilAsync(() => bobsTab.Messages.Count(m => m.MessageIdOf() == sent.MessageIdOf()) == 2);
        Assert.All(bobsTab.Messages, m => Assert.Equal(sent.MessageIdOf(), m.MessageIdOf()));
    }

    [Fact]
    public async Task A_retry_is_logged_with_ids_only()
    {
        var (alice, _, conversation) = await DirectChatAsync();
        var capture = new CapturingLoggerProvider();
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddLogging(logging => logging.AddProvider(capture))));
        using var client = Authorized(host, alice);
        var clientMessageId = Guid.NewGuid();
        var secret = $"secret-{Guid.NewGuid():N}";
        var sent = await client.SendMessageAsync(conversation, secret, clientMessageId);

        await client.PostMessageAsync(conversation, secret, clientMessageId);

        var line = Assert.Single(capture.Entries, e => e.Contains("A repeat of message"));
        Assert.Contains(sent.MessageIdOf().ToString(), line);
        Assert.DoesNotContain(capture.Entries, e => e.Contains(secret));
    }

    // ##### The index #####

    // Only a clash on this index means "already sent". Another unique violation is a different problem and must
    // be reported as one, not answered with someone else's message.
    [Fact]
    public void Only_a_clash_on_the_client_id_index_counts_as_a_repeat()
    {
        static DbUpdateException Failure(string sqlState, string? constraint) =>
            new("save failed", new PostgresException("duplicate", "ERROR", "ERROR", sqlState, constraintName: constraint));

        Assert.True(MessageRepository.IsClientMessageClash(
            Failure(PostgresErrorCodes.UniqueViolation, MessageConfiguration.ClientMessageIndexName)));
        Assert.False(MessageRepository.IsClientMessageClash(
            Failure(PostgresErrorCodes.UniqueViolation, "PK_Messages")));
        Assert.False(MessageRepository.IsClientMessageClash(
            Failure(PostgresErrorCodes.ForeignKeyViolation, MessageConfiguration.ClientMessageIndexName)));
        Assert.False(MessageRepository.IsClientMessageClash(new DbUpdateException("save failed")));
    }

    [Fact]
    public async Task The_database_itself_refuses_a_second_message_with_the_same_client_id_from_the_same_sender()
    {
        var (alice, _, conversation) = await DirectChatAsync();
        var clientMessageId = Guid.NewGuid();
        await _factory.AddMessageAsync(conversation, alice.Id, "first", DateTime.UtcNow.AddMinutes(-2), clientMessageId);

        var failure = await Record.ExceptionAsync(() =>
            _factory.AddMessageAsync(conversation, alice.Id, "second", DateTime.UtcNow.AddMinutes(-1), clientMessageId));

        var violation = Assert.IsType<PostgresException>(Assert.IsType<DbUpdateException>(failure).InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, violation.SqlState);
        Assert.Equal(MessageConfiguration.ClientMessageIndexName, violation.ConstraintName);
    }

    [Fact]
    public async Task Any_number_of_messages_without_a_client_id_can_share_a_sender_and_conversation()
    {
        var (alice, _, conversation) = await DirectChatAsync();

        for (var i = 0; i < 3; i++)
            await _factory.AddMessageAsync(conversation, alice.Id, $"plain {i}", DateTime.UtcNow.AddMinutes(-3 + i));

        Assert.Equal(3, await CountAsync(conversation, alice.Id));
    }

    [Fact]
    public async Task The_migration_adds_the_column_and_the_index_and_can_be_undone_without_losing_messages()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(_factory);
        var migration = scratch.MigrationNamed("AddMessageClientMessageId");
        var previous = scratch.MigrationBefore(migration);
        await scratch.MigrateToAsync(previous);
        Assert.False(await scratch.ColumnExistsAsync("Messages", "ClientMessageId"));

        await scratch.MigrateToAsync(migration);
        Assert.True(await scratch.ColumnExistsAsync("Messages", "ClientMessageId"));
        Assert.True(await scratch.IndexExistsAsync(MessageConfiguration.ClientMessageIndexName));

        // Unique, and only over messages that carry an id: the rest never enter the index, so it costs nothing
        // for them. (A unique index lets any number of NULLs through, so the filter changes no behaviour, which
        // is why only the definition can show it.)
        var definition = await scratch.IndexDefinitionAsync(MessageConfiguration.ClientMessageIndexName);
        Assert.Contains("UNIQUE", definition);
        Assert.Contains("WHERE (\"ClientMessageId\" IS NOT NULL)", definition);

        await scratch.MigrateToAsync(previous);
        Assert.False(await scratch.ColumnExistsAsync("Messages", "ClientMessageId"));
        Assert.False(await scratch.IndexExistsAsync(MessageConfiguration.ClientMessageIndexName));
        Assert.True(await scratch.TableExistsAsync("Messages"));
    }

    // ##### Helpers #####

    private async Task<(TestUser Alice, TestUser Bob, Guid Conversation)> DirectChatAsync()
    {
        var alice = await _factory.RegisterUserAsync("alice");
        var bob = await _factory.RegisterUserAsync("bob");
        using var client = _factory.CreateClientFor(alice);

        return (alice, bob, (await client.CreateDirectAsync(bob.Id)).IdOf());
    }

    private Task<int> CountAsync(Guid conversation, Guid sender)
        => _factory.QueryDbAsync(db => db.Messages.CountAsync(m => m.ConversationId == conversation && m.SenderId == sender));

    private static HttpClient Authorized(WebApplicationFactory<Program> host, TestUser user)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", user.Token);
        return client;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < until)
            await Task.Delay(25);

        Assert.True(condition(), "The condition was not met in time.");
    }
}
