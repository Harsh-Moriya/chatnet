using ChatNET.API.Auth.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChatNET.API.Auth.Persistence;

public class PasswordResetTokenConfiguration : IEntityTypeConfiguration<PasswordResetToken>
{
    public void Configure(EntityTypeBuilder<PasswordResetToken> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();

        builder.Property(t => t.TokenHash)
            .IsRequired()
            .HasColumnType("text");

        builder.Property(t => t.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(t => t.ExpiresAt).HasColumnType("timestamp with time zone");
        builder.Property(t => t.UsedAt).HasColumnType("timestamp with time zone");

        // The lookup when a link is used, and the guarantee that two tokens never share a hash.
        builder.HasIndex(t => t.TokenHash).IsUnique();

        // Voiding a person's other links is by user; the clean-up of old rows is by expiry date.
        builder.HasIndex(t => t.UserId);
        builder.HasIndex(t => t.ExpiresAt);

        builder.HasOne(t => t.User)
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
