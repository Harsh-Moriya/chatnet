using ChatNET.API.Auth.Models;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Messaging.Models;
using ChatNET.API.Tests.Infrastructure;

namespace ChatNET.API.Tests.Media;

// An attachment used to keep a link; it now keeps the key of the file in the bucket and the type the file
// was stored with. The column is renamed, not dropped and added, so nothing that was stored is lost.
[Collection(IntegrationTestCollection.Name)]
public class AttachmentMigrationTests
{
    private readonly ChatApiFactory _factory;

    public AttachmentMigrationTests(ChatApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task The_migration_renames_the_link_column_keeping_its_value_adds_the_type_and_can_be_undone()
    {
        await using var scratch = await ScratchDatabase.CreateAsync(_factory);
        var migration = scratch.MigrationNamed("AttachmentStorageKeyAndContentType");
        var previous = scratch.MigrationBefore(migration);
        await scratch.MigrateToAsync(previous);
        Assert.True(await scratch.ColumnExistsAsync("MessageAttachments", "Url"));
        Assert.False(await scratch.ColumnExistsAsync("MessageAttachments", "StorageKey"));

        var attachmentId = await ArrangeAttachmentWithLinkAsync(scratch, "https://old.example/files/report.pdf");

        await scratch.MigrateToAsync(migration);

        Assert.False(await scratch.ColumnExistsAsync("MessageAttachments", "Url"));
        Assert.True(await scratch.ColumnExistsAsync("MessageAttachments", "ContentType"));
        Assert.Equal("https://old.example/files/report.pdf", await scratch.ScalarAsync(
            $"SELECT \"StorageKey\" FROM \"MessageAttachments\" WHERE \"Id\" = '{attachmentId}'"));
        Assert.Equal("", await scratch.ScalarAsync(
            $"SELECT \"ContentType\" FROM \"MessageAttachments\" WHERE \"Id\" = '{attachmentId}'"));

        await scratch.MigrateToAsync(previous);

        Assert.False(await scratch.ColumnExistsAsync("MessageAttachments", "StorageKey"));
        Assert.False(await scratch.ColumnExistsAsync("MessageAttachments", "ContentType"));
        Assert.Equal("https://old.example/files/report.pdf", await scratch.ScalarAsync(
            $"SELECT \"Url\" FROM \"MessageAttachments\" WHERE \"Id\" = '{attachmentId}'"));
    }

    // The schema is the one before the migration, so the message is made with the model (which still matches
    // everything but this table) and the attachment with plain SQL.
    private static async Task<Guid> ArrangeAttachmentWithLinkAsync(ScratchDatabase scratch, string link)
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
        var message = new Message { Id = Guid.NewGuid(), ConversationId = conversation.Id, SenderId = user.Id, Content = "", CreatedAt = now };
        await scratch.AddAsync(conversation, message);

        var attachmentId = Guid.NewGuid();
        await scratch.ExecuteAsync(
            "INSERT INTO \"MessageAttachments\" (\"Id\", \"MessageId\", \"Url\", \"Type\", \"FileName\", \"FileSizeBytes\", \"CreatedAt\") " +
            $"VALUES ('{attachmentId}', '{message.Id}', '{link}', 'File', 'report.pdf', 1234, now())");

        return attachmentId;
    }
}
