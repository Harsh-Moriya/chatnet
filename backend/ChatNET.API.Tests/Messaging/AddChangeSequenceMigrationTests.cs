using ChatNET.API.Auth.Models;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Tests.Infrastructure;

namespace ChatNET.API.Tests.Messaging;

// From the migration on, every change of a message takes the next number of its conversation. The messages that exist before it are
// numbered in the order they were sent, and each conversation's counter is its number of messages, so the changes feed can start from
// what is already there.
[Collection(IntegrationTestCollection.Name)]
public class AddChangeSequenceMigrationTests
{
    private const string Migration = "AddChangeSequence";

    private readonly ChatApiFactory _factory;

    public AddChangeSequenceMigrationTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private static async Task<long> CountAsync(ScratchDatabase scratch, string table, string where = "TRUE") =>
        (long)(await scratch.ScalarAsync($"SELECT COUNT(*) FROM \"{table}\" WHERE {where}"))!;

    private sealed record Arranged(Guid Busy, Guid Quiet, Guid Empty, Guid[] BusyInOrder);

    // A conversation with four messages, two of them stamped at the same moment (the id settles their order), one of them deleted;
    // one with a single message; and one with none. Arranged in the schema as it was before the migration.
    private static async Task<Arranged> ArrangeAsync(ScratchDatabase scratch)
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
        var conversations = new List<Conversation>();
        foreach (var name in new[] { "Busy", "Quiet", "Empty" })
        {
            var conversation = new Conversation
            {
                Id = Guid.NewGuid(), Type = ConversationType.Group, Name = name, CreatedAt = now, UpdatedAt = now, CreatedById = user.Id,
            };
            await scratch.AddAsync(conversation);
            conversations.Add(conversation);
        }

        var sameMoment = now.AddMinutes(-3);
        var ids = new[]
        {
            Guid.Parse("00000000-0000-0000-0000-0000000000a4"),
            Guid.Parse("00000000-0000-0000-0000-0000000000a2"),
            Guid.Parse("00000000-0000-0000-0000-0000000000a3"),
            Guid.Parse("00000000-0000-0000-0000-0000000000a1"),
        };

        // Inserted out of order on purpose, so the numbers cannot come from the order of insertion, and with ids that run against the
        // times, so they cannot come from the ids alone. The two stamped alike are stored the larger id first.
        await Insert(scratch, conversations[0].Id, user.Id, ids[3], now.AddMinutes(-1), deleted: false);
        await Insert(scratch, conversations[0].Id, user.Id, ids[2], sameMoment, deleted: false);
        await Insert(scratch, conversations[0].Id, user.Id, ids[0], now.AddMinutes(-5), deleted: true);
        await Insert(scratch, conversations[0].Id, user.Id, ids[1], sameMoment, deleted: false);
        await Insert(scratch, conversations[1].Id, user.Id, Guid.NewGuid(), now.AddMinutes(-2), deleted: false);

        // ids[0] is the oldest and has the largest id, ids[1] and ids[2] share a time and the smaller id comes first, ids[3] is the newest
        // and has the smallest id.
        return new Arranged(conversations[0].Id, conversations[1].Id, conversations[2].Id, ids);
    }

    private static Task Insert(ScratchDatabase scratch, Guid conversation, Guid sender, Guid id, DateTime at, bool deleted) =>
        scratch.ExecuteAsync(
            "INSERT INTO \"Messages\" (\"Id\", \"ConversationId\", \"SenderId\", \"Ciphertext\", \"KeyEpoch\", \"ClientMessageId\", \"CreatedAt\", \"EditCount\", \"IsDeleted\") " +
            $"VALUES ('{id}', '{conversation}', '{sender}', {(deleted ? "NULL" : "decode(repeat('01', 98), 'hex')")}, 1, '{Guid.NewGuid()}', '{at:O}', 0, {(deleted ? "true" : "false")})");

    private async Task<(ScratchDatabase Scratch, Arranged Before)> MigratedAsync()
    {
        var scratch = await ScratchDatabase.CreateAsync(_factory);
        var migration = scratch.MigrationNamed(Migration);
        await scratch.MigrateToAsync(scratch.MigrationBefore(migration));
        var before = await ArrangeAsync(scratch);
        await scratch.MigrateToAsync(migration);
        return (scratch, before);
    }

    private static async Task<long> SeqOfAsync(ScratchDatabase scratch, Guid message) =>
        (long)(await scratch.ScalarAsync($"SELECT \"ChangeSeq\" FROM \"Messages\" WHERE \"Id\" = '{message}'"))!;

    [Fact]
    public async Task The_messages_of_a_conversation_are_numbered_in_the_order_they_were_sent()
    {
        var (scratch, before) = await MigratedAsync();
        await using var _ = scratch;

        Assert.Equal(1L, await SeqOfAsync(scratch, before.BusyInOrder[0]));
        Assert.Equal(2L, await SeqOfAsync(scratch, before.BusyInOrder[1]));
        Assert.Equal(3L, await SeqOfAsync(scratch, before.BusyInOrder[2]));
        Assert.Equal(4L, await SeqOfAsync(scratch, before.BusyInOrder[3]));
    }

    [Fact]
    public async Task Messages_stamped_alike_are_told_apart_by_their_id()
    {
        var (scratch, before) = await MigratedAsync();
        await using var _ = scratch;

        Assert.True(await SeqOfAsync(scratch, before.BusyInOrder[1]) < await SeqOfAsync(scratch, before.BusyInOrder[2]));
    }

    [Fact]
    public async Task Every_conversation_is_numbered_on_its_own()
    {
        var (scratch, before) = await MigratedAsync();
        await using var _ = scratch;

        var quiet = (long)(await scratch.ScalarAsync($"SELECT \"ChangeSeq\" FROM \"Messages\" WHERE \"ConversationId\" = '{before.Quiet}'"))!;
        Assert.Equal(1L, quiet);
    }

    [Fact]
    public async Task A_conversations_counter_is_its_number_of_messages_and_an_empty_one_is_at_zero()
    {
        var (scratch, before) = await MigratedAsync();
        await using var _ = scratch;

        Assert.Equal(4L, (long)(await scratch.ScalarAsync($"SELECT \"ChangeSeq\" FROM \"Conversations\" WHERE \"Id\" = '{before.Busy}'"))!);
        Assert.Equal(1L, (long)(await scratch.ScalarAsync($"SELECT \"ChangeSeq\" FROM \"Conversations\" WHERE \"Id\" = '{before.Quiet}'"))!);
        Assert.Equal(0L, (long)(await scratch.ScalarAsync($"SELECT \"ChangeSeq\" FROM \"Conversations\" WHERE \"Id\" = '{before.Empty}'"))!);
    }

    [Fact]
    public async Task A_deleted_message_is_numbered_like_any_other()
    {
        var (scratch, before) = await MigratedAsync();
        await using var _ = scratch;

        Assert.Equal(1L, await CountAsync(scratch, "Messages", $"\"Id\" = '{before.BusyInOrder[0]}' AND \"IsDeleted\" AND \"ChangeSeq\" = 1"));
    }

    [Fact]
    public async Task No_message_is_lost_or_changed_by_numbering_them()
    {
        var (scratch, _) = await MigratedAsync();
        await using var _scratch = scratch;

        Assert.Equal(5L, await CountAsync(scratch, "Messages"));
        Assert.Equal(1L, await CountAsync(scratch, "Messages", "\"IsDeleted\""));
    }

    // ##### The guarantee that stays #####

    [Fact]
    public async Task Two_messages_of_one_conversation_cannot_share_a_number()
    {
        var (scratch, before) = await MigratedAsync();
        await using var _ = scratch;

        var clash = await Assert.ThrowsAnyAsync<Exception>(() => InsertNumbered(scratch, before.Busy, 4));

        Assert.True(await scratch.IndexExistsAsync("IX_Messages_ConversationId_ChangeSeq"));
        Assert.Contains("IX_Messages_ConversationId_ChangeSeq", clash.ToString());
    }

    [Fact]
    public async Task A_message_stored_without_a_number_is_refused_not_stored_as_zero()
    {
        var (scratch, before) = await MigratedAsync();
        await using var _ = scratch;

        var refused = await Assert.ThrowsAnyAsync<Exception>(() => Insert(scratch, before.Busy, Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow, false));

        Assert.Contains("ChangeSeq", refused.ToString());
    }

    private static Task InsertNumbered(ScratchDatabase scratch, Guid conversation, long seq) =>
        scratch.ExecuteAsync(
            "INSERT INTO \"Messages\" (\"Id\", \"ConversationId\", \"SenderId\", \"Ciphertext\", \"KeyEpoch\", \"ClientMessageId\", \"CreatedAt\", \"EditCount\", \"IsDeleted\", \"ChangeSeq\") " +
            $"SELECT '{Guid.NewGuid()}', '{conversation}', \"SenderId\", decode(repeat('01', 98), 'hex'), 1, '{Guid.NewGuid()}', now(), 0, false, {seq} " +
            $"FROM \"Messages\" WHERE \"ConversationId\" = '{conversation}' LIMIT 1");

    [Fact]
    public async Task Undoing_takes_the_numbers_and_the_index_away_and_keeps_the_messages()
    {
        var (scratch, _) = await MigratedAsync();
        await using var _scratch = scratch;

        await scratch.MigrateToAsync(scratch.MigrationBefore(scratch.MigrationNamed(Migration)));

        Assert.False(await scratch.ColumnExistsAsync("Messages", "ChangeSeq"));
        Assert.False(await scratch.ColumnExistsAsync("Conversations", "ChangeSeq"));
        Assert.False(await scratch.IndexExistsAsync("IX_Messages_ConversationId_ChangeSeq"));
        Assert.Equal(5L, await CountAsync(scratch, "Messages"));
    }
}
