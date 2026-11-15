using ChatNET.API.Calls.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChatNET.API.Calls.Persistence;

public class CallConfiguration : IEntityTypeConfiguration<Call>
{
    public void Configure(EntityTypeBuilder<Call> builder)
    {
        builder.ToTable("Calls", table =>
        {
            table.HasCheckConstraint("CK_Calls_Times", "\"StartedAt\" <= \"EndedAt\" AND (\"AnsweredAt\" IS NULL OR \"AnsweredAt\" BETWEEN \"StartedAt\" AND \"EndedAt\")");

            // Completed means someone answered, and a call that was turned down, given up on, left to ring out or met a busy line
            // was not answered. Failed may be either: a connection can break before the answer or after it.
            table.HasCheckConstraint(
                "CK_Calls_Answer",
                "(\"EndReason\" = 'Completed' AND \"AnsweredAt\" IS NOT NULL) " +
                "OR (\"EndReason\" IN ('Missed', 'Declined', 'Cancelled', 'Busy') AND \"AnsweredAt\" IS NULL) " +
                "OR \"EndReason\" = 'Failed'");

            table.HasCheckConstraint("CK_Calls_People", "\"CallerId\" <> \"CalleeId\"");
        });

        builder.HasKey(c => c.Id);

        // Made by the application when the call starts, so the live state in Redis and this row share it.
        builder.Property(c => c.Id).ValueGeneratedNever();

        builder.Property(c => c.Media).HasConversion<string>().HasMaxLength(8).IsRequired();
        builder.Property(c => c.EndReason).HasConversion<string>().HasMaxLength(16).IsRequired();

        builder.Property(c => c.StartedAt).HasColumnType("timestamp with time zone");
        builder.Property(c => c.AnsweredAt).HasColumnType("timestamp with time zone");
        builder.Property(c => c.EndedAt).HasColumnType("timestamp with time zone");

        builder.HasOne(c => c.Conversation)
            .WithMany()
            .HasForeignKey(c => c.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(c => c.Caller)
            .WithMany()
            .HasForeignKey(c => c.CallerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(c => c.Callee)
            .WithMany()
            .HasForeignKey(c => c.CalleeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
