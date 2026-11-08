using ChatNET.API.Keys.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChatNET.API.Keys.Persistence;

public class AccountKeyBundleConfiguration : IEntityTypeConfiguration<AccountKeyBundle>
{
    public void Configure(EntityTypeBuilder<AccountKeyBundle> builder)
    {
        builder.ToTable("AccountKeyBundles");

        // One row per generation of a person's keys. The key is also what makes setting up twice a conflict and not a duplicate.
        builder.HasKey(b => new { b.UserId, b.Generation });

        builder.Property(b => b.Bundle).IsRequired().HasColumnType("bytea");
        builder.Property(b => b.CreatedAt).HasColumnType("timestamp with time zone");

        builder.HasOne(b => b.User)
            .WithMany()
            .HasForeignKey(b => b.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class KeyBackupConfiguration : IEntityTypeConfiguration<KeyBackup>
{
    public void Configure(EntityTypeBuilder<KeyBackup> builder)
    {
        builder.ToTable("KeyBackups");

        builder.HasKey(b => b.UserId);

        builder.Property(b => b.Blob).IsRequired().HasColumnType("bytea");
        builder.Property(b => b.UpdatedAt).HasColumnType("timestamp with time zone");

        builder.HasOne(b => b.User)
            .WithMany()
            .HasForeignKey(b => b.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
