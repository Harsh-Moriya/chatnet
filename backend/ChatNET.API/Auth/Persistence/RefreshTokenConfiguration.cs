using ChatNET.API.Auth.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChatNET.API.Auth.Persistence;

public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();

        builder.Property(t => t.TokenHash)
            .IsRequired()
            .HasColumnType("text");

        builder.Property(t => t.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(t => t.ExpiresAt).HasColumnType("timestamp with time zone");
        builder.Property(t => t.RevokedAt).HasColumnType("timestamp with time zone");

        // The lookup on every refresh, and the guarantee that two tokens never share a hash.
        builder.HasIndex(t => t.TokenHash).IsUnique();

        // Revoking a family touches every token in it; deleting a user's tokens is by user.
        builder.HasIndex(t => t.FamilyId);
        builder.HasIndex(t => t.UserId);

        // The clean-up of old rows is by expiry date.
        builder.HasIndex(t => t.ExpiresAt);

        builder.HasOne(t => t.User)
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
