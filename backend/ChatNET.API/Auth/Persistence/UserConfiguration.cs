using ChatNET.API.Auth.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChatNET.API.Auth.Persistence;

// Identity already configures the base IdentityUser columns (Id, UserName, Email,
// PasswordHash, etc.) in IdentityDbContext.OnModelCreating. This class only
// configures the custom properties we added to AppUser.
public class UserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> builder)
    {
        builder.Property(u => u.DisplayName)
            .IsRequired()
            .HasColumnType("text");

        builder.Property(u => u.AvatarUrl)
            .HasColumnType("text");

        builder.Property(u => u.LastSeenAt)
            .HasColumnType("timestamp with time zone");

        builder.Property(u => u.IsDeactivated)
            .IsRequired()
            .HasDefaultValue(false);

        builder.Property(u => u.DeactivatedAt)
            .HasColumnType("timestamp with time zone");
    }
}
