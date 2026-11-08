using ChatNET.API.Auth.Models;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Tests.Infrastructure;

namespace ChatNET.API.Tests.Conversations;

// Conversations get a key period and a flag, and two tables hold the periods and the envelopes. A conversation that existed before
// is at epoch 0 with nothing flagged, and the database itself refuses the shapes the application never writes.
[Collection(IntegrationTestCollection.Name)]
public class KeyEpochMigrationTests
{
    private readonly ChatApiFactory _factory;

    public KeyEpochMigrationTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task The_migration_leaves_existing_conversations_at_epoch_zero_and_can_be_undone()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(_factory);
        var migration = scratch.MigrationNamed("AddConversationKeyEpochs");
        var previous = scratch.MigrationBefore(migration);
        await scratch.MigrateToAsync(previous);
        Assert.False(await scratch.ColumnExistsAsync("Conversations", "KeyEpoch"));
        var (conversation, _) = await ArrangeConversationAsync(scratch);

        await scratch.MigrateToAsync(migration);

        Assert.Equal(0, await scratch.ScalarAsync($"SELECT \"KeyEpoch\" FROM \"Conversations\" WHERE \"Id\" = '{conversation}'"));
        Assert.Equal(false, await scratch.ScalarAsync($"SELECT \"KeyRotationRequired\" FROM \"Conversations\" WHERE \"Id\" = '{conversation}'"));
        Assert.True(await scratch.TableExistsAsync("ConversationKeyEpochs"));
        Assert.True(await scratch.TableExistsAsync("ConversationKeyEnvelopes"));
        Assert.True(await scratch.IndexExistsAsync("IX_ConversationKeyEnvelopes_ConversationId_UserId_Epoch"));

        await scratch.MigrateToAsync(previous);

        Assert.False(await scratch.ColumnExistsAsync("Conversations", "KeyEpoch"));
        Assert.False(await scratch.ColumnExistsAsync("Conversations", "KeyRotationRequired"));
        Assert.False(await scratch.TableExistsAsync("ConversationKeyEpochs"));
        Assert.False(await scratch.TableExistsAsync("ConversationKeyEnvelopes"));
        Assert.Equal(1L, await scratch.ScalarAsync($"SELECT COUNT(*) FROM \"Conversations\" WHERE \"Id\" = '{conversation}'"));
    }

    [Fact]
    public async Task The_database_refuses_an_epoch_number_below_one_and_a_negative_current_epoch()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(_factory);
        await scratch.MigrateToAsync(scratch.MigrationNamed("AddConversationKeyEpochs"));
        var (conversation, user) = await ArrangeConversationAsync(scratch);

        var epochZero = await Record.ExceptionAsync(() => scratch.ExecuteAsync(EpochInsert(conversation, user, epoch: 0)));
        var negative = await Record.ExceptionAsync(() =>
            scratch.ExecuteAsync($"UPDATE \"Conversations\" SET \"KeyEpoch\" = -1 WHERE \"Id\" = '{conversation}'"));

        Assert.NotNull(epochZero);
        Assert.NotNull(negative);
    }

    [Theory]
    [InlineData(31, 64)]
    [InlineData(33, 64)]
    [InlineData(32, 63)]
    [InlineData(32, 65)]
    public async Task The_database_refuses_a_commitment_or_a_signature_of_the_wrong_size(int commitment, int signature)
    {
        await using var scratch = await ScratchDatabase.CreateAsync(_factory);
        await scratch.MigrateToAsync(scratch.MigrationNamed("AddConversationKeyEpochs"));
        var (conversation, user) = await ArrangeConversationAsync(scratch);

        var failure = await Record.ExceptionAsync(() =>
            scratch.ExecuteAsync(EpochInsert(conversation, user, epoch: 1, commitmentBytes: commitment, signatureBytes: signature)));

        Assert.NotNull(failure);
    }

    [Theory]
    [InlineData(93, 1)]
    [InlineData(95, 1)]
    [InlineData(94, 0)]
    public async Task The_database_refuses_an_envelope_of_the_wrong_size_or_for_generation_zero(int envelopeBytes, int generation)
    {
        await using var scratch = await ScratchDatabase.CreateAsync(_factory);
        await scratch.MigrateToAsync(scratch.MigrationNamed("AddConversationKeyEpochs"));
        var (conversation, user) = await ArrangeConversationAsync(scratch);
        await scratch.ExecuteAsync(EpochInsert(conversation, user, epoch: 1));

        var failure = await Record.ExceptionAsync(() => scratch.ExecuteAsync(
            $"INSERT INTO \"ConversationKeyEnvelopes\" (\"ConversationId\", \"Epoch\", \"UserId\", \"AccountKeyGeneration\", \"Envelope\") " +
            $"VALUES ('{conversation}', 1, '{user}', {generation}, decode('{new string('0', envelopeBytes * 2)}', 'hex'))"));

        Assert.NotNull(failure);
    }

    [Fact]
    public async Task An_envelope_cannot_exist_without_its_epoch_and_goes_with_it()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(_factory);
        await scratch.MigrateToAsync(scratch.MigrationNamed("AddConversationKeyEpochs"));
        var (conversation, user) = await ArrangeConversationAsync(scratch);
        var envelope = $"INSERT INTO \"ConversationKeyEnvelopes\" (\"ConversationId\", \"Epoch\", \"UserId\", \"AccountKeyGeneration\", \"Envelope\") " +
                       $"VALUES ('{conversation}', 1, '{user}', 1, decode('{new string('0', 188)}', 'hex'))";

        var orphan = await Record.ExceptionAsync(() => scratch.ExecuteAsync(envelope));
        await scratch.ExecuteAsync(EpochInsert(conversation, user, epoch: 1));
        await scratch.ExecuteAsync(envelope);
        await scratch.ExecuteAsync($"DELETE FROM \"Conversations\" WHERE \"Id\" = '{conversation}'");

        Assert.NotNull(orphan);
        Assert.Equal(0L, await scratch.ScalarAsync("SELECT COUNT(*) FROM \"ConversationKeyEpochs\""));
        Assert.Equal(0L, await scratch.ScalarAsync("SELECT COUNT(*) FROM \"ConversationKeyEnvelopes\""));
    }

    private static string EpochInsert(Guid conversation, Guid user, int epoch, int commitmentBytes = 32, int signatureBytes = 64) =>
        "INSERT INTO \"ConversationKeyEpochs\" (\"ConversationId\", \"Epoch\", \"CreatedById\", \"Reason\", \"CreatedAt\", \"KeyCommitment\", \"Signature\") " +
        $"VALUES ('{conversation}', {epoch}, '{user}', 'Created', now(), decode('{new string('0', commitmentBytes * 2)}', 'hex'), " +
        $"decode('{new string('0', signatureBytes * 2)}', 'hex'))";

    // A person and a conversation of theirs, made with the application's model at a schema the model still matches (the scratch
    // database adds the columns the model writes for the time of the insert).
    private static async Task<(Guid Conversation, Guid User)> ArrangeConversationAsync(ScratchDatabase scratch)
    {
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            UserName = $"u_{Guid.NewGuid():N}"[..20],
            NormalizedUserName = $"U_{Guid.NewGuid():N}"[..20].ToUpperInvariant(),
            Email = $"{Guid.NewGuid():N}@example.test",
            NormalizedEmail = $"{Guid.NewGuid():N}@EXAMPLE.TEST".ToUpperInvariant(),
            DisplayName = "Someone",
            SecurityStamp = Guid.NewGuid().ToString(),
        };
        await scratch.InsertAsync(user);

        var now = DateTime.UtcNow;
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(), Type = ConversationType.Group, Name = "Team", CreatedAt = now, UpdatedAt = now, CreatedById = user.Id,
        };
        await scratch.AddAsync(conversation);

        return (conversation.Id, user.Id);
    }
}
