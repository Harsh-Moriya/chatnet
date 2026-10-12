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

        // Identity creates this index without making it unique, and checks "is the email taken?" in code
        // before inserting. Two requests at once can both pass that check, so the database has to be the
        // one to refuse the second. The index is on the normalised (upper-case) email, so addresses that
        // differ only in case are the same address. Same name as Identity's, so this changes the
        // existing index instead of adding a second one.
        builder.HasIndex(u => u.NormalizedEmail)
            .HasDatabaseName("EmailIndex")
            .IsUnique();
    }
}
