using ChatNET.API.Devices.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChatNET.API.Devices.Persistence;

public class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    public const int MaxNameLength = 100;

    public void Configure(EntityTypeBuilder<Device> builder)
    {
        builder.ToTable("Devices");

        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();

        builder.Property(d => d.Kind).HasConversion<string>().HasMaxLength(16);
        builder.Property(d => d.Name).IsRequired().HasMaxLength(MaxNameLength);

        builder.Property(d => d.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(d => d.LastActiveAt).HasColumnType("timestamp with time zone");
        builder.Property(d => d.RemovedAt).HasColumnType("timestamp with time zone");

        // A person's devices: the list, and the count against the limit.
        builder.HasIndex(d => d.UserId);

        builder.HasOne(d => d.User)
            .WithMany()
            .HasForeignKey(d => d.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class DeviceLinkRequestConfiguration : IEntityTypeConfiguration<DeviceLinkRequest>
{
    public const int CodeLength = 10;

    public void Configure(EntityTypeBuilder<DeviceLinkRequest> builder)
    {
        builder.ToTable("DeviceLinkRequests");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();

        builder.Property(r => r.Code).IsRequired().HasMaxLength(CodeLength);
        builder.Property(r => r.TemporaryPublicKey).IsRequired().HasColumnType("bytea");
        builder.Property(r => r.Kind).HasConversion<string>().HasMaxLength(16);
        builder.Property(r => r.Name).IsRequired().HasMaxLength(DeviceConfiguration.MaxNameLength);
        builder.Property(r => r.Payload).HasColumnType("bytea");

        builder.Property(r => r.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(r => r.ExpiresAt).HasColumnType("timestamp with time zone");
        builder.Property(r => r.ApprovedAt).HasColumnType("timestamp with time zone");
        builder.Property(r => r.CollectedAt).HasColumnType("timestamp with time zone");

        // The lookup by code is the person's own, and a code is never reused by two of their open requests.
        builder.HasIndex(r => new { r.UserId, r.Code }).IsUnique();

        // The clean-up of old requests is by expiry.
        builder.HasIndex(r => r.ExpiresAt);

        builder.HasOne(r => r.User)
            .WithMany()
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
