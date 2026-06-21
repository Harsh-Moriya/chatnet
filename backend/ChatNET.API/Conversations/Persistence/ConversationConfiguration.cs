using ChatNET.API.Conversations.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChatNET.API.Conversations.Persistence;

public class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasDefaultValueSql("gen_random_uuid()");

        // Store enum as a string so the database column is human-readable in queries.
        // The alternative (integer) is faster but impossible to read without the enum definition.
        builder.Property(c => c.Type)
            .HasConversion<string>()
            .IsRequired();

        builder.Property(c => c.Name).HasColumnType("text");
        builder.Property(c => c.AvatarUrl).HasColumnType("text");
        builder.Property(c => c.DirectKey).HasColumnType("text");

        builder.Property(c => c.CreatedAt)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(c => c.UpdatedAt)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        // Partial unique index: only one Direct conversation can exist per pair of users.
        // A partial index (WHERE DirectKey IS NOT NULL) means Group conversations with a
        // null DirectKey are not subject to the uniqueness constraint.
        builder.HasIndex(c => c.DirectKey)
            .IsUnique()
            .HasFilter("\"DirectKey\" IS NOT NULL");

        // Covers the sidebar query: SELECT conversations ORDER BY UpdatedAt DESC.
        builder.HasIndex(c => c.UpdatedAt)
            .IsDescending(true);

        // Enforces at the DB level that every Direct conversation has a DirectKey.
        // The constraint in C# (DirectKey generation) is the first line of defence;
        // this is the guarantee that no code path can bypass it.
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_Conversations_DirectType_DirectKey",
            "\"Type\" != 'Direct' OR \"DirectKey\" IS NOT NULL"));

        builder.HasOne(c => c.CreatedBy)
            .WithMany()
            .HasForeignKey(c => c.CreatedById)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(c => c.Members)
            .WithOne(m => m.Conversation)
            .HasForeignKey(m => m.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
