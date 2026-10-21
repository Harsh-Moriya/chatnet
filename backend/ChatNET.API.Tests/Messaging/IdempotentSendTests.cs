using ChatNET.API.Messaging.Persistence;
using ChatNET.API.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
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

    // ##### The index #####

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
}
