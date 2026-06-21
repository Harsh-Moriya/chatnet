using ChatNET.API.Messaging.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChatNET.API.Messaging.Persistence;

public class MessageConfiguration : IEntityTypeConfiguration<Message>
{
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
