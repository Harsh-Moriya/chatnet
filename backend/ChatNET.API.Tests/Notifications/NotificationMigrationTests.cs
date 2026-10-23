using ChatNET.API.Auth.Models;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Messaging.Models;
using ChatNET.API.Tests.Infrastructure;

namespace ChatNET.API.Tests.Notifications;

// A notification can now point at the message it is about. The migration adds the column, an index on it and a
// foreign key that lets the message go without taking the notification with it.
[Collection(IntegrationTestCollection.Name)]
public class NotificationMigrationTests
{
    private readonly ChatApiFactory _factory;

    public NotificationMigrationTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task The_migration_adds_the_message_column_keeps_existing_rows_and_can_be_undone()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(_factory);
        var migration = scratch.MigrationNamed("AddNotificationMessageId");
        var previous = scratch.MigrationBefore(migration);
        await scratch.MigrateToAsync(previous);
        Assert.False(await scratch.ColumnExistsAsync("Notifications", "MessageId"));
        var (notification, _) = await ArrangeNotificationAsync(scratch);

        await scratch.MigrateToAsync(migration);

        Assert.True(await scratch.ColumnExistsAsync("Notifications", "MessageId"));
        Assert.True(await scratch.IndexExistsAsync("IX_Notifications_MessageId"));
        Assert.Equal(DBNull.Value, await scratch.ScalarAsync($"SELECT \"MessageId\" FROM \"Notifications\" WHERE \"Id\" = '{notification}'"));

        await scratch.MigrateToAsync(previous);

        Assert.False(await scratch.ColumnExistsAsync("Notifications", "MessageId"));
        Assert.False(await scratch.IndexExistsAsync("IX_Notifications_MessageId"));
        Assert.Equal(1L, await scratch.ScalarAsync($"SELECT COUNT(*) FROM \"Notifications\" WHERE \"Id\" = '{notification}'"));
    }

    // Messages are only ever soft-deleted by the application, but the rule is the database's: a notification
    // outlives the message it was about, with nothing to point at.
    [Fact]
    public async Task Removing_the_message_leaves_the_notification_with_no_message()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(_factory);
        await scratch.MigrateToAsync(scratch.MigrationNamed("AddNotificationMessageId"));
        var (notification, message) = await ArrangeNotificationAsync(scratch);
        await scratch.ExecuteAsync($"UPDATE \"Notifications\" SET \"MessageId\" = '{message}' WHERE \"Id\" = '{notification}'");
        Assert.Equal(message, await scratch.ScalarAsync($"SELECT \"MessageId\" FROM \"Notifications\" WHERE \"Id\" = '{notification}'"));

        await scratch.ExecuteAsync($"DELETE FROM \"Messages\" WHERE \"Id\" = '{message}'");

        Assert.Equal(DBNull.Value, await scratch.ScalarAsync($"SELECT \"MessageId\" FROM \"Notifications\" WHERE \"Id\" = '{notification}'"));
        Assert.Equal(1L, await scratch.ScalarAsync($"SELECT COUNT(*) FROM \"Notifications\" WHERE \"Id\" = '{notification}'"));
    }

    [Fact]
    public async Task A_message_id_that_is_not_a_message_is_refused()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(_factory);
        await scratch.MigrateToAsync(scratch.MigrationNamed("AddNotificationMessageId"));
        var (notification, _) = await ArrangeNotificationAsync(scratch);

        var failure = await Record.ExceptionAsync(() =>
            scratch.ExecuteAsync($"UPDATE \"Notifications\" SET \"MessageId\" = '{Guid.NewGuid()}' WHERE \"Id\" = '{notification}'"));

        Assert.NotNull(failure);
    }

    // The notification is made with plain SQL because the schema may be the one before the column existed. The
    // message is made through the model, which matches everything but this table.
    private static async Task<(Guid Notification, Guid Message)> ArrangeNotificationAsync(ScratchDatabase scratch)
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
            Id = Guid.NewGuid(), Type = ConversationType.Group, Name = "Mentions", CreatedAt = now, UpdatedAt = now, CreatedById = user.Id,
        };
        var message = new Message { Id = Guid.NewGuid(), ConversationId = conversation.Id, SenderId = user.Id, Content = "hi", CreatedAt = now };
        await scratch.AddAsync(conversation, message);

        var notification = Guid.NewGuid();
        await scratch.ExecuteAsync(
            "INSERT INTO \"Notifications\" (\"Id\", \"UserId\", \"ActorId\", \"ConversationId\", \"Type\", \"IsRead\", \"CreatedAt\") " +
            $"VALUES ('{notification}', '{user.Id}', '{user.Id}', '{conversation.Id}', 'Mention', false, now())");

        return (notification, message.Id);
    }
}
