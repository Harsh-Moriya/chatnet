using ChatNET.API.Messaging.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChatNET.API.Messaging.Persistence;

public class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    // Named so the code that sends a message can tell this unique index apart from any other when it
    // catches a violation.
    public const string ClientMessageIndexName = "IX_Messages_ConversationId_SenderId_ClientMessageId";

    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).HasDefaultValueSql("gen_random_uuid()");

        // Content is nullable by design: on soft-delete the content is cleared but the
        // row is kept so the message timeline has no gaps.
        builder.Property(m => m.Content).HasColumnType("text");

        builder.Property(m => m.CreatedAt)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(m => m.EditedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(m => m.DeletedAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(m => m.EditCount)
            .IsRequired()
            .HasDefaultValue(0);

        builder.Property(m => m.IsDeleted)
            .IsRequired()
            .HasDefaultValue(false);

        // Primary query pattern: fetch the N most recent messages in a conversation.
        // The composite index covers both the filter (ConversationId) and the sort
        // (CreatedAt DESC) in a single index scan with no additional sort step.
        builder.HasIndex(m => new { m.ConversationId, m.CreatedAt })
            .IsDescending(false, true);

        // One message per (conversation, sender, client id). A send that arrives twice, because the client
        // retried after a timeout whose first attempt had in fact succeeded, finds the first message through
        // this index instead of storing a second. The database is the arbiter: two copies of the same send
        // arriving at the same moment cannot both be inserted. Partial so that messages sent without a client
        // id (every older message, and any client that sends none) are not in the index at all. Postgres
        // already lets any number of nulls through a unique index, so the filter changes no behaviour; it
        // keeps the index small.
        builder.HasIndex(m => new { m.ConversationId, m.SenderId, m.ClientMessageId })
            .IsUnique()
            .HasFilter("\"ClientMessageId\" IS NOT NULL")
            .HasDatabaseName(ClientMessageIndexName);

        builder.HasOne(m => m.Conversation)
            .WithMany()
            .HasForeignKey(m => m.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(m => m.Sender)
            .WithMany()
            .HasForeignKey(m => m.SenderId)
            .OnDelete(DeleteBehavior.Restrict);

        // Separate FK for admin deletes (DeletedById may differ from SenderId).
        builder.HasOne(m => m.DeletedBy)
            .WithMany()
            .HasForeignKey(m => m.DeletedById)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired(false);

        builder.HasMany(m => m.Attachments)
            .WithOne(a => a.Message)
            .HasForeignKey(a => a.MessageId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
