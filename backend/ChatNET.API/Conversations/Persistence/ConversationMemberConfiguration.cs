using ChatNET.API.Conversations.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChatNET.API.Conversations.Persistence;

public class ConversationMemberConfiguration : IEntityTypeConfiguration<ConversationMember>
{
    public void Configure(EntityTypeBuilder<ConversationMember> builder)
    {
        // Composite primary key: a user can only be a member of a conversation once.
        builder.HasKey(m => new { m.ConversationId, m.UserId });

        builder.Property(m => m.Role)
            .HasConversion<string>()
            .IsRequired();

        builder.Property(m => m.JoinedAt)
            .HasColumnType("timestamp with time zone")
            .IsRequired();

        builder.Property(m => m.LeftAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(m => m.LastReadAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(m => m.IsMuted)
            .IsRequired()
            .HasDefaultValue(false);

        builder.HasOne(m => m.User)
            .WithMany()
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
