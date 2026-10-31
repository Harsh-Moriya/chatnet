using ChatNET.API.Outbox.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChatNET.API.Outbox.Persistence;

public class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public const string PendingIndexName = "IX_OutboxMessages_CreatedAt_Pending";
    public const string DispatchedIndexName = "IX_OutboxMessages_DispatchedAt";

    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages");

        builder.HasKey(o => o.Id);

        // Made by the application, because the rows that say "this message was created" are written before the
        // message is, in the same save, and carry its id.
        builder.Property(o => o.Id).ValueGeneratedNever();

        builder.Property(o => o.Kind).IsRequired().HasMaxLength(64);
        builder.Property(o => o.Payload).IsRequired().HasColumnType("jsonb");
        builder.Property(o => o.LastError).HasMaxLength(500);

        builder.Property(o => o.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(o => o.NextAttemptAt).HasColumnType("timestamp with time zone");
        builder.Property(o => o.DispatchedAt).HasColumnType("timestamp with time zone");

        // What the relay walks: the rows not yet pushed, oldest first. Partial, so the many rows that were pushed
        // (and are kept for a day) are not in it and the walk never steps over them.
        builder.HasIndex(o => o.CreatedAt)
            .HasFilter("\"DispatchedAt\" IS NULL")
            .HasDatabaseName(PendingIndexName);

        // What the clean-up walks: the rows that were pushed, by when.
        builder.HasIndex(o => o.DispatchedAt)
            .HasFilter("\"DispatchedAt\" IS NOT NULL")
            .HasDatabaseName(DispatchedIndexName);
    }
}
