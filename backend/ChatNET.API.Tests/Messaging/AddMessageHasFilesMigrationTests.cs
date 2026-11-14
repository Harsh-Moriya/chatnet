using ChatNET.API.Auth.Models;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Messaging.Persistence;
using ChatNET.API.Tests.Infrastructure;
using Npgsql;

namespace ChatNET.API.Tests.Messaging;

// A message says whether it has files, so the gallery can find those that do by an index. The messages that exist before the migration
// are marked from what is stored, and a message stored afterwards must say it, since the column has no default to fall back on.
[Collection(IntegrationTestCollection.Name)]
public class AddMessageHasFilesMigrationTests
{
    private const string Migration = "AddMessageHasFiles";

    private readonly ChatApiFactory _factory;

    public AddMessageHasFilesMigrationTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private sealed record Arranged(Guid Conversation, Guid User, Guid WithFiles, Guid WithoutFiles);

    // Arranged in the schema as it was before the migration: a message with two files, and one with none.
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
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(), Type = ConversationType.Group, Name = "Files", CreatedAt = now, UpdatedAt = now, CreatedById = user.Id,
        };
        await scratch.AddAsync(conversation);

        var withFiles = Guid.NewGuid();
        var withoutFiles = Guid.NewGuid();
        await InsertMessage(scratch, conversation.Id, user.Id, withFiles, now.AddMinutes(-2), seq: 1);
        await InsertMessage(scratch, conversation.Id, user.Id, withoutFiles, now.AddMinutes(-1), seq: 2);
        foreach (var position in new[] { 0, 1 })
        {
            await scratch.ExecuteAsync(
                "INSERT INTO \"MessageAttachments\" (\"Id\", \"MessageId\", \"StorageKey\", \"FileSizeBytes\", \"CreatedAt\") " +
                $"VALUES ('{Guid.NewGuid()}', '{withFiles}', 'attachments/{conversation.Id}/{Guid.NewGuid()}', 100, now() + interval '{position} microseconds')");
        }

        return new Arranged(conversation.Id, user.Id, withFiles, withoutFiles);
    }

    private static Task InsertMessage(ScratchDatabase scratch, Guid conversation, Guid sender, Guid id, DateTime at, long seq) =>
        scratch.ExecuteAsync(
            "INSERT INTO \"Messages\" (\"Id\", \"ConversationId\", \"SenderId\", \"Ciphertext\", \"KeyEpoch\", \"ClientMessageId\", \"CreatedAt\", \"EditCount\", \"IsDeleted\", \"ChangeSeq\") " +
            $"VALUES ('{id}', '{conversation}', '{sender}', decode(repeat('01', 98), 'hex'), 1, '{Guid.NewGuid()}', '{at:O}', 0, false, {seq})");

    private async Task<(ScratchDatabase Scratch, Arranged Before)> MigratedAsync()
    {
        var scratch = await ScratchDatabase.CreateAsync(_factory);
        var migration = scratch.MigrationNamed(Migration);
        await scratch.MigrateToAsync(scratch.MigrationBefore(migration));
        var before = await ArrangeAsync(scratch);
        await scratch.MigrateToAsync(migration);
        return (scratch, before);
    }

    private static async Task<bool> HasFilesAsync(ScratchDatabase scratch, Guid message) =>
        (bool)(await scratch.ScalarAsync($"SELECT \"HasFiles\" FROM \"Messages\" WHERE \"Id\" = '{message}'"))!;

    [Fact]
    public async Task A_message_that_has_files_is_marked_and_one_that_has_none_is_not()
    {
        var (scratch, before) = await MigratedAsync();
        await using var _ = scratch;

        Assert.True(await HasFilesAsync(scratch, before.WithFiles));
        Assert.False(await HasFilesAsync(scratch, before.WithoutFiles));
    }

    [Fact]
    public async Task The_index_holds_only_the_messages_with_files_that_are_not_deleted()
    {
        var (scratch, _) = await MigratedAsync();
        await using var _ = scratch;

        var definition = await scratch.IndexDefinitionAsync(MessageConfiguration.FilesIndexName);

        Assert.Contains("HasFiles", definition);
        Assert.Contains("NOT \"IsDeleted\"", definition);
    }

    [Fact]
    public async Task A_message_stored_afterwards_has_to_say_whether_it_has_files()
    {
        var (scratch, before) = await MigratedAsync();
        await using var _ = scratch;

        var error = await Assert.ThrowsAnyAsync<Exception>(() => scratch.ExecuteAsync(
            "INSERT INTO \"Messages\" (\"Id\", \"ConversationId\", \"SenderId\", \"Ciphertext\", \"KeyEpoch\", \"ClientMessageId\", \"CreatedAt\", \"EditCount\", \"IsDeleted\", \"ChangeSeq\") " +
            $"VALUES ('{Guid.NewGuid()}', '{before.Conversation}', '{before.User}', decode(repeat('01', 98), 'hex'), 1, '{Guid.NewGuid()}', now(), 0, false, 3)"));

        Assert.IsType<PostgresException>(error.InnerException ?? error);
    }

    [Fact]
    public async Task The_migration_can_be_undone()
    {
        var (scratch, _) = await MigratedAsync();
        await using var _ = scratch;

        await scratch.MigrateToAsync(scratch.MigrationBefore(scratch.MigrationNamed(Migration)));

        Assert.False(await scratch.ColumnExistsAsync("Messages", "HasFiles"));
        Assert.False(await scratch.IndexExistsAsync(MessageConfiguration.FilesIndexName));
    }
}
