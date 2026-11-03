using ChatNET.API.Push.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChatNET.API.Push.Persistence;

public class PushSubscriptionConfiguration : IEntityTypeConfiguration<PushSubscription>
{
    public const int MaxEndpointLength = 2048;

    public void Configure(EntityTypeBuilder<PushSubscription> builder)
    {
        builder.ToTable("PushSubscriptions");

        builder.HasKey(s => s.Id);

        // Made by the application: a save is an upsert on the endpoint, which has to name the id of a row it may insert.
        builder.Property(s => s.Id).ValueGeneratedNever();

        builder.Property(s => s.Endpoint).IsRequired().HasMaxLength(MaxEndpointLength);
        builder.Property(s => s.P256dh).IsRequired().HasMaxLength(128);
        builder.Property(s => s.Auth).IsRequired().HasMaxLength(64);

        builder.Property(s => s.CreatedAt).HasColumnType("timestamp with time zone");
        builder.Property(s => s.LastUsedAt).HasColumnType("timestamp with time zone");

        // One row per browser. A browser that another person signs in on registers its endpoint again and the row moves to
        // them; the index is what makes that one atomic statement (INSERT ... ON CONFLICT) and not a race.
        builder.HasIndex(s => s.Endpoint).IsUnique();

        // A person's browsers: who a push goes to, and which to drop when they have more than the limit.
        builder.HasIndex(s => s.UserId);

        builder.HasOne(s => s.User)
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
