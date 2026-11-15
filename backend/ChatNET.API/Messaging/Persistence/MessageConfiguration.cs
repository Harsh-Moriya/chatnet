using ChatNET.API.Messaging.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChatNET.API.Messaging.Persistence;

public class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    // Named so the code that sends a message can tell this unique index apart from any other when it
    // catches a violation.
    public const string ClientMessageIndexName = "IX_Messages_ConversationId_SenderId_ClientMessageId";

    // Named so a test can find the index, and so it reads as what it is: the guarantee that two changes of one conversation never
    // share a number, whatever code path made them.
    public const string ChangeSeqIndexName = "IX_Messages_ConversationId_ChangeSeq";

    // The messages that have files and are not deleted, newest first: what the gallery pages through.
    public const string FilesIndexName = "IX_Messages_ConversationId_CreatedAt_Files";

    // The guarantee that a call is in the timeline once, whatever wrote it.
    public const string CallIndexName = "IX_Messages_CallId";

    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).HasDefaultValueSql("gen_random_uuid()");

        // For a Text row: null exactly when the message is deleted: the text is removed and the row is kept so the message timeline has
        // no gaps. A message that is not deleted holds a blob of a size the formats allow (see MessageBlob), whatever is inside it.
        // A Call row never has one.
        builder.Property(m => m.Ciphertext).HasColumnType("bytea");
        builder.Property(m => m.KeyEpoch).IsRequired();
        builder.Property(m => m.ClientMessageId).IsRequired();
        builder.Property(m => m.MentionedUserIds).HasColumnType("uuid[]").IsRequired().HasDefaultValueSql("'{}'");

        // What someone said (Text) or the row of a finished call (Call). The call row's id is made by the server and it carries no
        // text, so it is the one kind that is not deleted and has no ciphertext.
        builder.Property(m => m.Kind).HasConversion<string>().HasMaxLength(8).IsRequired().HasDefaultValue(MessageKind.Text);

        builder.ToTable(table =>
        {
            table.HasCheckConstraint(
                "CK_Messages_Content",
                $"(\"Kind\" = 'Text' AND \"CallId\" IS NULL AND ((\"IsDeleted\" AND \"Ciphertext\" IS NULL) OR (NOT \"IsDeleted\" AND \"Ciphertext\" IS NOT NULL AND length(\"Ciphertext\") BETWEEN {MessageBlob.MinBytes} AND {MessageBlob.MaxBytes}))) " +
                "OR (\"Kind\" = 'Call' AND \"CallId\" IS NOT NULL AND \"Ciphertext\" IS NULL AND NOT \"IsDeleted\" AND \"EditCount\" = 0)");

            // Nothing is sealed in a call row, and a conversation may have no key period yet (0), so only text must name one.
            table.HasCheckConstraint("CK_Messages_KeyEpoch", "\"Kind\" = 'Call' OR \"KeyEpoch\" >= 1");
        });

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
        // arriving at the same moment cannot both be inserted.
        builder.HasIndex(m => new { m.ConversationId, m.SenderId, m.ClientMessageId })
            .IsUnique()
            .HasDatabaseName(ClientMessageIndexName);

        // The changes feed reads a conversation's messages in the order of their latest change, from a cursor. Unique, because
        // every change takes a number from the conversation's counter: a path that forgot to would collide here instead of
        // silently hiding a change from the clients that follow the feed.
        builder.Property(m => m.ChangeSeq).IsRequired();
        builder.HasIndex(m => new { m.ConversationId, m.ChangeSeq })
            .IsUnique()
            .HasDatabaseName(ChangeSeqIndexName);

        // A partial index over only the messages the gallery lists, in the order it lists them, so a page costs about its own rows
        // however long the conversation is and however few of its messages have files. The query must repeat the filter exactly.
        builder.Property(m => m.HasFiles).IsRequired();
        builder.HasIndex(m => new { m.ConversationId, m.CreatedAt, m.Id })
            .IsDescending(false, true, true)
            .HasFilter("\"HasFiles\" AND NOT \"IsDeleted\"")
            .HasDatabaseName(FilesIndexName);

        builder.HasOne(m => m.Conversation)
            .WithMany()
            .HasForeignKey(m => m.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        // A call has one row in the timeline, and the row goes with the call.
        builder.HasIndex(m => m.CallId)
            .IsUnique()
            .HasFilter("\"CallId\" IS NOT NULL")
            .HasDatabaseName(CallIndexName);

        builder.HasOne(m => m.Call)
            .WithOne()
            .HasForeignKey<Message>(m => m.CallId)
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
