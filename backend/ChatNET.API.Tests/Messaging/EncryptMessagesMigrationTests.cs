using ChatNET.API.Auth.Models;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Tests.Infrastructure;

namespace ChatNET.API.Tests.Messaging;

// From the migration on, the server holds only what the browser of the sender sealed. The messages that exist before it are held in the
// clear, so the migration deletes them, with what hangs on them, and makes sure the bucket is emptied of their files.
[Collection(IntegrationTestCollection.Name)]
public class EncryptMessagesMigrationTests
{
    private readonly ChatApiFactory _factory;

    public EncryptMessagesMigrationTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    private sealed record Arranged(Guid User, Guid Conversation, Guid Message, Guid Mention, Guid Invitation, string[] Keys);

    private static async Task<long> CountAsync(ScratchDatabase scratch, string table, string where = "TRUE") =>
        (long)(await scratch.ScalarAsync($"SELECT COUNT(*) FROM \"{table}\" WHERE {where}"))!;

    // A conversation with a message that has two files, a deleted message, a mention of the first, an invitation to the group, and the
    // outbox rows of all of it, in the schema as it was before the migration.
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

        var message = Guid.NewGuid();
        var deleted = Guid.NewGuid();
        await scratch.ExecuteAsync(
            "INSERT INTO \"Messages\" (\"Id\", \"ConversationId\", \"SenderId\", \"Content\", \"CreatedAt\", \"EditCount\", \"IsDeleted\") " +
            $"VALUES ('{message}', '{conversation.Id}', '{user.Id}', 'said in the clear', now(), 0, false), " +
            $"('{deleted}', '{conversation.Id}', '{user.Id}', NULL, now(), 0, true)");

        var keys = new[] { $"attachments/{conversation.Id}/{Guid.NewGuid()}", $"attachments/{conversation.Id}/{Guid.NewGuid()}" };
        foreach (var key in keys)
        {
            await scratch.ExecuteAsync(
                "INSERT INTO \"MessageAttachments\" (\"Id\", \"MessageId\", \"StorageKey\", \"Type\", \"FileName\", \"ContentType\", \"FileSizeBytes\", \"CreatedAt\") " +
                $"VALUES ('{Guid.NewGuid()}', '{message}', '{key}', 'Image', 'photo.png', 'image/png', 100, now())");
        }

        var mention = Guid.NewGuid();
        var invitation = Guid.NewGuid();
        await scratch.ExecuteAsync(
            "INSERT INTO \"Notifications\" (\"Id\", \"UserId\", \"ActorId\", \"ConversationId\", \"MessageId\", \"Type\", \"IsRead\", \"CreatedAt\") " +
            $"VALUES ('{mention}', '{user.Id}', '{user.Id}', '{conversation.Id}', '{message}', 'Mention', false, now()), " +
            $"('{invitation}', '{user.Id}', '{user.Id}', '{conversation.Id}', NULL, 'MemberAdded', false, now())");

        await Outbox(scratch, "MessageCreated", $"jsonb_build_object('messageId', '{message}', 'conversationId', '{conversation.Id}')");
        await Outbox(scratch, "MessageUpdated", $"jsonb_build_object('messageId', '{message}', 'conversationId', '{conversation.Id}')");
        await Outbox(scratch, "MessageFilesRemoved", $"jsonb_build_object('messageId', '{deleted}')");
        await Outbox(scratch, "MessageAlertRequested",
            $"jsonb_build_object('messageId', '{message}', 'conversationId', '{conversation.Id}', 'mentionedUserIds', '[]'::jsonb)");
        await Outbox(scratch, "NotificationCreated", $"jsonb_build_object('notificationId', '{mention}', 'userId', '{user.Id}')");
        await Outbox(scratch, "NotificationAlertRequested", $"jsonb_build_object('notificationId', '{mention}', 'userId', '{user.Id}')");
        await Outbox(scratch, "NotificationCreated", $"jsonb_build_object('notificationId', '{invitation}', 'userId', '{user.Id}')");
        await Outbox(scratch, "PasswordResetRequested", "jsonb_build_object('userId', NULL)");

        return new Arranged(user.Id, conversation.Id, message, mention, invitation, keys);
    }

    // The payload is an SQL expression for a jsonb value, because raw SQL here takes no braces.
    private static Task Outbox(ScratchDatabase scratch, string kind, string payload) =>
        scratch.ExecuteAsync(
            "INSERT INTO \"OutboxMessages\" (\"Id\", \"Kind\", \"Payload\", \"CreatedAt\", \"NextAttemptAt\", \"Attempts\") " +
            $"VALUES ('{Guid.NewGuid()}', '{kind}', {payload}, now(), now(), 0)");

    // ##### What the migration deletes #####

    [Fact]
    public async Task The_messages_that_exist_are_deleted_with_their_files_and_mentions()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(_factory);
        var migration = scratch.MigrationNamed("EncryptMessages");
        await scratch.MigrateToAsync(scratch.MigrationBefore(migration));
        var before = await ArrangeAsync(scratch);
        Assert.Equal(2L, await CountAsync(scratch, "Messages"));

        await scratch.MigrateToAsync(migration);

        Assert.Equal(0L, await CountAsync(scratch, "Messages"));
        Assert.Equal(0L, await CountAsync(scratch, "MessageAttachments"));
        Assert.Equal(0L, await CountAsync(scratch, "Notifications", $"\"Id\" = '{before.Mention}'"));
        Assert.Equal(1L, await CountAsync(scratch, "Notifications", $"\"Id\" = '{before.Invitation}'"));
        Assert.Equal(1L, await CountAsync(scratch, "Conversations", $"\"Id\" = '{before.Conversation}'"));
    }

    [Fact]
    public async Task The_pushes_about_the_messages_and_the_mention_are_deleted_and_the_others_stay()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(_factory);
        var migration = scratch.MigrationNamed("EncryptMessages");
        await scratch.MigrateToAsync(scratch.MigrationBefore(migration));
        var before = await ArrangeAsync(scratch);

        await scratch.MigrateToAsync(migration);

        foreach (var kind in new[] { "MessageCreated", "MessageUpdated", "MessageFilesRemoved", "MessageAlertRequested", "NotificationAlertRequested" })
            Assert.Equal(0L, await CountAsync(scratch, "OutboxMessages", $"\"Kind\" = '{kind}'"));

        Assert.Equal(0L, await CountAsync(scratch, "OutboxMessages", $"\"Payload\" ->> 'notificationId' = '{before.Mention}'"));
        Assert.Equal(1L, await CountAsync(scratch, "OutboxMessages", $"\"Payload\" ->> 'notificationId' = '{before.Invitation}'"));
        Assert.Equal(1L, await CountAsync(scratch, "OutboxMessages", "\"Kind\" = 'PasswordResetRequested'"));
    }

    [Fact]
    public async Task Every_file_of_a_deleted_message_is_queued_for_removal_from_the_bucket()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(_factory);
        var migration = scratch.MigrationNamed("EncryptMessages");
        await scratch.MigrateToAsync(scratch.MigrationBefore(migration));
        var before = await ArrangeAsync(scratch);

        await scratch.MigrateToAsync(migration);

        Assert.Equal(2L, await CountAsync(scratch, "OutboxMessages", "\"Kind\" = 'StoredFileRemoved'"));
        foreach (var key in before.Keys)
            Assert.Equal(1L, await CountAsync(scratch, "OutboxMessages", $"\"Kind\" = 'StoredFileRemoved' AND \"Payload\" ->> 'key' = '{key}'"));

        Assert.Equal(2L, await CountAsync(scratch, "OutboxMessages",
            "\"Kind\" = 'StoredFileRemoved' AND \"DispatchedAt\" IS NULL AND \"NextAttemptAt\" <= now() AND \"Attempts\" = 0"));
    }

    [Fact]
    public async Task A_database_with_no_messages_migrates_and_queues_nothing()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(_factory);
        var migration = scratch.MigrationNamed("EncryptMessages");
        await scratch.MigrateToAsync(scratch.MigrationBefore(migration));

        await scratch.MigrateToAsync(migration);

        Assert.Equal(0L, await CountAsync(scratch, "OutboxMessages", "\"Kind\" = 'StoredFileRemoved'"));
    }

    // ##### What the table holds from then on #####

    [Fact]
    public async Task The_table_holds_a_blob_an_epoch_and_the_names_and_no_text()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(_factory);
        await scratch.MigrateToAsync(scratch.MigrationNamed("EncryptMessages"));

        Assert.False(await scratch.ColumnExistsAsync("Messages", "Content"));
        Assert.True(await scratch.ColumnExistsAsync("Messages", "Ciphertext"));
        Assert.True(await scratch.ColumnExistsAsync("Messages", "KeyEpoch"));
        Assert.True(await scratch.ColumnExistsAsync("Messages", "MentionedUserIds"));
        Assert.Equal("NO", await scratch.ScalarAsync(
            "SELECT is_nullable FROM information_schema.columns WHERE table_name = 'Messages' AND column_name = 'ClientMessageId'"));
        Assert.Equal("NO", await scratch.ScalarAsync(
            "SELECT is_nullable FROM information_schema.columns WHERE table_name = 'Messages' AND column_name = 'KeyEpoch'"));
        Assert.Equal(DBNull.Value, await scratch.ScalarAsync(
            "SELECT column_default FROM information_schema.columns WHERE table_name = 'Messages' AND column_name = 'KeyEpoch'"));
    }

    [Fact]
    public async Task The_client_id_index_is_unique_for_every_message_and_not_only_the_ones_that_had_an_id()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(_factory);
        await scratch.MigrateToAsync(scratch.MigrationNamed("EncryptMessages"));

        var definition = await scratch.IndexDefinitionAsync("IX_Messages_ConversationId_SenderId_ClientMessageId");

        Assert.Contains("UNIQUE", definition);
        Assert.DoesNotContain("WHERE", definition);
    }

    [Theory]
    [InlineData("a blob that is too short", "decode(repeat('01', 97), 'hex')", 1, false, "ok")]
    [InlineData("a blob that is too long", "decode(repeat('01', 32769), 'hex')", 1, false, "ok")]
    [InlineData("an epoch below one", "decode(repeat('01', 98), 'hex')", 0, false, "ok")]
    [InlineData("a message that is not deleted and has no blob", "NULL", 1, false, "ok")]
    [InlineData("a deleted message that kept its blob", "decode(repeat('01', 98), 'hex')", 1, true, "ok")]
    [InlineData("a message with no client id", "decode(repeat('01', 98), 'hex')", 1, false, "no client id")]
    public async Task The_database_refuses(string _, string ciphertext, int epoch, bool deleted, string clientId)
    {
        await using var scratch = await ScratchDatabase.CreateAsync(_factory);
        await scratch.MigrateToAsync(scratch.MigrationNamed("EncryptMessages"));
        var user = await ArrangeConversationAsync(scratch);

        var failure = await Record.ExceptionAsync(() => scratch.ExecuteAsync(InsertMessage(user, ciphertext, epoch, deleted, clientId == "ok" ? $"'{Guid.NewGuid()}'" : "NULL")));

        Assert.NotNull(failure);
    }

    [Fact]
    public async Task The_database_accepts_a_blob_of_the_smallest_and_the_largest_size_and_a_deleted_message_without_one()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(_factory);
        await scratch.MigrateToAsync(scratch.MigrationNamed("EncryptMessages"));
        var user = await ArrangeConversationAsync(scratch);

        await scratch.ExecuteAsync(InsertMessage(user, "decode(repeat('01', 98), 'hex')", 1, false, $"'{Guid.NewGuid()}'"));
        await scratch.ExecuteAsync(InsertMessage(user, "decode(repeat('01', 32768), 'hex')", 2, false, $"'{Guid.NewGuid()}'"));
        await scratch.ExecuteAsync(InsertMessage(user, "NULL", 1, true, $"'{Guid.NewGuid()}'"));

        Assert.Equal(3L, await CountAsync(scratch, "Messages"));
    }

    [Fact]
    public async Task One_client_id_cannot_be_used_twice_by_one_sender_in_one_conversation()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(_factory);
        await scratch.MigrateToAsync(scratch.MigrationNamed("EncryptMessages"));
        var user = await ArrangeConversationAsync(scratch);
        var clientId = $"'{Guid.NewGuid()}'";
        await scratch.ExecuteAsync(InsertMessage(user, "decode(repeat('01', 98), 'hex')", 1, false, clientId));

        var failure = await Record.ExceptionAsync(() => scratch.ExecuteAsync(InsertMessage(user, "decode(repeat('01', 98), 'hex')", 1, false, clientId)));

        Assert.NotNull(failure);
    }

    // ##### Undoing #####

    [Fact]
    public async Task Undoing_puts_the_columns_and_the_index_back_and_brings_nothing_back()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(_factory);
        var migration = scratch.MigrationNamed("EncryptMessages");
        var previous = scratch.MigrationBefore(migration);
        await scratch.MigrateToAsync(previous);
        await ArrangeAsync(scratch);
        await scratch.MigrateToAsync(migration);

        await scratch.MigrateToAsync(previous);

        Assert.True(await scratch.ColumnExistsAsync("Messages", "Content"));
        Assert.False(await scratch.ColumnExistsAsync("Messages", "Ciphertext"));
        Assert.False(await scratch.ColumnExistsAsync("Messages", "KeyEpoch"));
        Assert.False(await scratch.ColumnExistsAsync("Messages", "MentionedUserIds"));
        Assert.Contains("WHERE", await scratch.IndexDefinitionAsync("IX_Messages_ConversationId_SenderId_ClientMessageId"));
        Assert.Equal(0L, await CountAsync(scratch, "Messages"));
    }

    // ##### Arranging #####

    private static async Task<(Guid User, Guid Conversation)> ArrangeConversationAsync(ScratchDatabase scratch)
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
            Id = Guid.NewGuid(), Type = ConversationType.Group, Name = "After", CreatedAt = now, UpdatedAt = now, CreatedById = user.Id,
        };
        await scratch.AddAsync(conversation);

        return (user.Id, conversation.Id);
    }

    private static string InsertMessage((Guid User, Guid Conversation) where, string ciphertext, int epoch, bool deleted, string clientId) =>
        "INSERT INTO \"Messages\" (\"Id\", \"ConversationId\", \"SenderId\", \"Ciphertext\", \"KeyEpoch\", \"CreatedAt\", \"ClientMessageId\", \"EditCount\", \"IsDeleted\") " +
        $"VALUES ('{Guid.NewGuid()}', '{where.Conversation}', '{where.User}', {ciphertext}, {epoch}, now(), {clientId}, 0, {(deleted ? "true" : "false")})";
}
