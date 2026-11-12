using ChatNET.API.Auth.Models;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Tests.Infrastructure;

namespace ChatNET.API.Tests.Media;

// From the migration on, a file is stored encrypted and its name and type are inside the sealed message, so the table keeps neither. The
// files that exist before it were stored as uploaded and named in the clear, so the migration deletes them and the messages that carry
// them, and makes sure the bucket is emptied of the objects. Messages with text only are not touched.
[Collection(IntegrationTestCollection.Name)]
public class EncryptFilesMigrationTests
{
    private const string Migration = "EncryptFiles";

    private readonly ChatApiFactory _factory;

    public EncryptFilesMigrationTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private sealed record Arranged(
        Guid User, Guid Conversation, Guid WithFiles, Guid Deleted, Guid TextOnly,
        Guid FileMention, Guid TextMention, Guid Invitation, string[] Keys);

    private static async Task<long> CountAsync(ScratchDatabase scratch, string table, string where = "TRUE") =>
        (long)(await scratch.ScalarAsync($"SELECT COUNT(*) FROM \"{table}\" WHERE {where}"))!;

    // A conversation with a message of two files, a deleted message that still has its file row, a message of text only, a mention in the
    // first and in the last, an invitation to the group, and the outbox rows of all of it, in the schema as it was before the migration.
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
            Id = Guid.NewGuid(), Type = ConversationType.Group, Name = "Before", CreatedAt = now, UpdatedAt = now, CreatedById = user.Id,
        };
        await scratch.AddAsync(conversation);

        var withFiles = Guid.NewGuid();
        var deleted = Guid.NewGuid();
        var textOnly = Guid.NewGuid();
        await scratch.ExecuteAsync(
            "INSERT INTO \"Messages\" (\"Id\", \"ConversationId\", \"SenderId\", \"Ciphertext\", \"KeyEpoch\", \"ClientMessageId\", \"CreatedAt\", \"EditCount\", \"IsDeleted\") " +
            $"VALUES ('{withFiles}', '{conversation.Id}', '{user.Id}', decode(repeat('01', 98), 'hex'), 1, '{Guid.NewGuid()}', now(), 0, false), " +
            $"('{deleted}', '{conversation.Id}', '{user.Id}', NULL, 1, '{Guid.NewGuid()}', now(), 0, true), " +
            $"('{textOnly}', '{conversation.Id}', '{user.Id}', decode(repeat('01', 98), 'hex'), 1, '{Guid.NewGuid()}', now(), 0, false)");

        var keys = new[]
        {
            $"attachments/{conversation.Id}/{Guid.NewGuid()}", $"attachments/{conversation.Id}/{Guid.NewGuid()}",
            $"attachments/{conversation.Id}/{Guid.NewGuid()}",
        };
        var owners = new[] { withFiles, withFiles, deleted };
        for (var i = 0; i < keys.Length; i++)
        {
            await scratch.ExecuteAsync(
                "INSERT INTO \"MessageAttachments\" (\"Id\", \"MessageId\", \"StorageKey\", \"Type\", \"FileName\", \"ContentType\", \"FileSizeBytes\", \"CreatedAt\") " +
                $"VALUES ('{Guid.NewGuid()}', '{owners[i]}', '{keys[i]}', 'Image', 'photo.png', 'image/png', 100, now())");
        }

        var fileMention = Guid.NewGuid();
        var textMention = Guid.NewGuid();
        var invitation = Guid.NewGuid();
        await scratch.ExecuteAsync(
            "INSERT INTO \"Notifications\" (\"Id\", \"UserId\", \"ActorId\", \"ConversationId\", \"MessageId\", \"Type\", \"IsRead\", \"CreatedAt\") " +
            $"VALUES ('{fileMention}', '{user.Id}', '{user.Id}', '{conversation.Id}', '{withFiles}', 'Mention', false, now()), " +
            $"('{textMention}', '{user.Id}', '{user.Id}', '{conversation.Id}', '{textOnly}', 'Mention', false, now()), " +
            $"('{invitation}', '{user.Id}', '{user.Id}', '{conversation.Id}', NULL, 'MemberAdded', false, now())");

        await Outbox(scratch, "MessageCreated", $"jsonb_build_object('messageId', '{withFiles}', 'conversationId', '{conversation.Id}')");
        await Outbox(scratch, "MessageCreated", $"jsonb_build_object('messageId', '{textOnly}', 'conversationId', '{conversation.Id}')");
        await Outbox(scratch, "MessageUpdated", $"jsonb_build_object('messageId', '{withFiles}', 'conversationId', '{conversation.Id}')");
        await Outbox(scratch, "MessageFilesRemoved", $"jsonb_build_object('messageId', '{deleted}')");
        await Outbox(scratch, "MessageAlertRequested",
            $"jsonb_build_object('messageId', '{withFiles}', 'conversationId', '{conversation.Id}', 'mentionedUserIds', '[]'::jsonb)");
        await Outbox(scratch, "MessageAlertRequested",
            $"jsonb_build_object('messageId', '{textOnly}', 'conversationId', '{conversation.Id}', 'mentionedUserIds', '[]'::jsonb)");
        await Outbox(scratch, "NotificationCreated", $"jsonb_build_object('notificationId', '{fileMention}', 'userId', '{user.Id}')");
        await Outbox(scratch, "NotificationAlertRequested", $"jsonb_build_object('notificationId', '{fileMention}', 'userId', '{user.Id}')");
        await Outbox(scratch, "NotificationCreated", $"jsonb_build_object('notificationId', '{textMention}', 'userId', '{user.Id}')");
        await Outbox(scratch, "NotificationCreated", $"jsonb_build_object('notificationId', '{invitation}', 'userId', '{user.Id}')");
        await Outbox(scratch, "PasswordResetRequested", "jsonb_build_object('userId', NULL)");

        return new Arranged(user.Id, conversation.Id, withFiles, deleted, textOnly, fileMention, textMention, invitation, keys);
    }

    // The payload is an SQL expression for a jsonb value, because raw SQL here takes no braces.
    private static Task Outbox(ScratchDatabase scratch, string kind, string payload) =>
        scratch.ExecuteAsync(
            "INSERT INTO \"OutboxMessages\" (\"Id\", \"Kind\", \"Payload\", \"CreatedAt\", \"NextAttemptAt\", \"Attempts\") " +
            $"VALUES ('{Guid.NewGuid()}', '{kind}', {payload}, now(), now(), 0)");

    private async Task<(ScratchDatabase Scratch, Arranged Before)> MigratedAsync()
    {
        var scratch = await ScratchDatabase.CreateAsync(_factory);
        var migration = scratch.MigrationNamed(Migration);
        await scratch.MigrateToAsync(scratch.MigrationBefore(migration));
        var before = await ArrangeAsync(scratch);
        await scratch.MigrateToAsync(migration);
        return (scratch, before);
    }

    // ##### What the migration deletes #####

    [Fact]
    public async Task The_messages_that_carry_files_are_deleted_with_their_file_rows_and_mentions_and_the_others_stay()
    {
        var (scratch, before) = await MigratedAsync();
        await using var _ = scratch;

        Assert.Equal(0L, await CountAsync(scratch, "Messages", $"\"Id\" IN ('{before.WithFiles}', '{before.Deleted}')"));
        Assert.Equal(1L, await CountAsync(scratch, "Messages", $"\"Id\" = '{before.TextOnly}'"));
        Assert.Equal(0L, await CountAsync(scratch, "MessageAttachments"));
        Assert.Equal(0L, await CountAsync(scratch, "Notifications", $"\"Id\" = '{before.FileMention}'"));
        Assert.Equal(1L, await CountAsync(scratch, "Notifications", $"\"Id\" = '{before.TextMention}'"));
        Assert.Equal(1L, await CountAsync(scratch, "Notifications", $"\"Id\" = '{before.Invitation}'"));
        Assert.Equal(1L, await CountAsync(scratch, "Conversations", $"\"Id\" = '{before.Conversation}'"));
    }

    [Fact]
    public async Task The_pushes_about_the_deleted_messages_and_their_mention_are_deleted_and_the_others_stay()
    {
        var (scratch, before) = await MigratedAsync();
        await using var _ = scratch;

        foreach (var kind in new[] { "MessageUpdated", "MessageFilesRemoved", "NotificationAlertRequested" })
            Assert.Equal(0L, await CountAsync(scratch, "OutboxMessages", $"\"Kind\" = '{kind}'"));

        Assert.Equal(0L, await CountAsync(scratch, "OutboxMessages", $"\"Payload\" ->> 'messageId' = '{before.WithFiles}'"));
        Assert.Equal(0L, await CountAsync(scratch, "OutboxMessages", $"\"Payload\" ->> 'notificationId' = '{before.FileMention}'"));
        Assert.Equal(1L, await CountAsync(scratch, "OutboxMessages", $"\"Kind\" = 'MessageCreated' AND \"Payload\" ->> 'messageId' = '{before.TextOnly}'"));
        Assert.Equal(1L, await CountAsync(scratch, "OutboxMessages", $"\"Kind\" = 'MessageAlertRequested' AND \"Payload\" ->> 'messageId' = '{before.TextOnly}'"));
        Assert.Equal(1L, await CountAsync(scratch, "OutboxMessages", $"\"Payload\" ->> 'notificationId' = '{before.TextMention}'"));
        Assert.Equal(1L, await CountAsync(scratch, "OutboxMessages", $"\"Payload\" ->> 'notificationId' = '{before.Invitation}'"));
        Assert.Equal(1L, await CountAsync(scratch, "OutboxMessages", "\"Kind\" = 'PasswordResetRequested'"));
    }

    [Fact]
    public async Task Every_file_that_existed_is_queued_for_removal_from_the_bucket()
    {
        var (scratch, before) = await MigratedAsync();
        await using var _ = scratch;

        Assert.Equal(3L, await CountAsync(scratch, "OutboxMessages", "\"Kind\" = 'StoredFileRemoved'"));
        foreach (var key in before.Keys)
            Assert.Equal(1L, await CountAsync(scratch, "OutboxMessages", $"\"Kind\" = 'StoredFileRemoved' AND \"Payload\" ->> 'key' = '{key}'"));

        Assert.Equal(3L, await CountAsync(scratch, "OutboxMessages",
            "\"Kind\" = 'StoredFileRemoved' AND \"DispatchedAt\" IS NULL AND \"NextAttemptAt\" <= now() AND \"Attempts\" = 0"));
    }

    [Fact]
    public async Task A_database_with_no_files_migrates_keeps_its_messages_and_queues_nothing()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(_factory);
        var migration = scratch.MigrationNamed(Migration);
        await scratch.MigrateToAsync(scratch.MigrationBefore(migration));
        var before = await ArrangeAsync(scratch);
        await scratch.ExecuteAsync("DELETE FROM \"MessageAttachments\"");
        await scratch.ExecuteAsync("DELETE FROM \"Messages\" WHERE \"IsDeleted\" OR \"Id\" = '" + before.WithFiles + "'");
        await scratch.ExecuteAsync("DELETE FROM \"OutboxMessages\" WHERE \"Kind\" = 'MessageFilesRemoved'");

        await scratch.MigrateToAsync(migration);

        Assert.Equal(1L, await CountAsync(scratch, "Messages"));
        Assert.Equal(0L, await CountAsync(scratch, "OutboxMessages", "\"Kind\" = 'StoredFileRemoved'"));
    }

    // ##### What the table holds from then on #####

    [Fact]
    public async Task The_table_keeps_the_key_and_the_size_and_neither_a_name_nor_a_type()
    {
        var (scratch, _) = await MigratedAsync();
        await using var _ = scratch;

        Assert.True(await scratch.ColumnExistsAsync("MessageAttachments", "StorageKey"));
        Assert.True(await scratch.ColumnExistsAsync("MessageAttachments", "FileSizeBytes"));
        Assert.False(await scratch.ColumnExistsAsync("MessageAttachments", "FileName"));
        Assert.False(await scratch.ColumnExistsAsync("MessageAttachments", "ContentType"));
        Assert.False(await scratch.ColumnExistsAsync("MessageAttachments", "Type"));
    }

    [Fact]
    public async Task Undoing_puts_the_columns_back_and_brings_nothing_back()
    {
        var (scratch, _) = await MigratedAsync();
        await using var _ = scratch;

        await scratch.MigrateToAsync(scratch.MigrationBefore(scratch.MigrationNamed(Migration)));

        Assert.True(await scratch.ColumnExistsAsync("MessageAttachments", "FileName"));
        Assert.True(await scratch.ColumnExistsAsync("MessageAttachments", "ContentType"));
        Assert.True(await scratch.ColumnExistsAsync("MessageAttachments", "Type"));
        Assert.Equal(0L, await CountAsync(scratch, "MessageAttachments"));
    }
}
