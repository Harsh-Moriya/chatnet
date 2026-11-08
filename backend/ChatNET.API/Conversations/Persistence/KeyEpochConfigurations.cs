using ChatNET.API.Auth.Models;
using ChatNET.API.Conversations.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChatNET.API.Conversations.Persistence;

public class ConversationKeyEpochConfiguration : IEntityTypeConfiguration<ConversationKeyEpoch>
{
    public void Configure(EntityTypeBuilder<ConversationKeyEpoch> builder)
    {
        builder.ToTable("ConversationKeyEpochs", t =>
        {
            t.HasCheckConstraint("CK_ConversationKeyEpochs_Epoch", "\"Epoch\" >= 1");
            t.HasCheckConstraint("CK_ConversationKeyEpochs_KeyCommitment", "length(\"KeyCommitment\") = 32");
            t.HasCheckConstraint("CK_ConversationKeyEpochs_Signature", "length(\"Signature\") = 64");
        });

        // The key is also what stops two requests from both storing "the next epoch".
        builder.HasKey(e => new { e.ConversationId, e.Epoch });

        builder.Property(e => e.Reason).HasConversion<string>().IsRequired();
        builder.Property(e => e.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(e => e.KeyCommitment).IsRequired().HasColumnType("bytea");
        builder.Property(e => e.Signature).IsRequired().HasColumnType("bytea");

        builder.HasOne<Conversation>()
            .WithMany()
            .HasForeignKey(e => e.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(e => e.CreatedById)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(e => e.Envelopes)
            .WithOne()
            .HasForeignKey(v => new { v.ConversationId, v.Epoch })
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class ConversationKeyEnvelopeConfiguration : IEntityTypeConfiguration<ConversationKeyEnvelope>
{
    public void Configure(EntityTypeBuilder<ConversationKeyEnvelope> builder)
    {
        builder.ToTable("ConversationKeyEnvelopes", t =>
        {
            t.HasCheckConstraint("CK_ConversationKeyEnvelopes_AccountKeyGeneration", "\"AccountKeyGeneration\" >= 1");
            t.HasCheckConstraint("CK_ConversationKeyEnvelopes_Envelope", "length(\"Envelope\") = 94");
        });

        builder.HasKey(v => new { v.ConversationId, v.Epoch, v.UserId });

        builder.Property(v => v.Envelope).IsRequired().HasColumnType("bytea");

        // What a member asks for: their envelopes in one conversation, oldest first.
        builder.HasIndex(v => new { v.ConversationId, v.UserId, v.Epoch });

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(v => v.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
