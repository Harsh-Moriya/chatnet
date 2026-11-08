using ChatNET.API.Auth.Models;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Devices.Models;
using ChatNET.API.Keys.Models;
using ChatNET.API.Messaging.Models;
using ChatNET.API.Notifications.Models;
using ChatNET.API.Outbox.Models;
using ChatNET.API.Push.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Common.Persistence;

// IdentityDbContext<AppUser, IdentityRole<Guid>, Guid> generates all the Identity tables
// (AspNetUsers, AspNetRoles, AspNetUserRoles, etc.) through its own OnModelCreating.
// We inherit from it so those tables are created in the same migration as our tables.
public class AppDbContext : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMember> ConversationMembers => Set<ConversationMember>();
    public DbSet<ConversationKeyEpoch> ConversationKeyEpochs => Set<ConversationKeyEpoch>();
    public DbSet<ConversationKeyEnvelope> ConversationKeyEnvelopes => Set<ConversationKeyEnvelope>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageAttachment> MessageAttachments => Set<MessageAttachment>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();
    public DbSet<AccountKeyBundle> AccountKeyBundles => Set<AccountKeyBundle>();
    public DbSet<KeyBackup> KeyBackups => Set<KeyBackup>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceLinkRequest> DeviceLinkRequests => Set<DeviceLinkRequest>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        // Must be called first: populates Identity's table configuration before ours runs.
        base.OnModelCreating(builder);

        // Discovers and applies every IEntityTypeConfiguration<T> class in this assembly
        // automatically. Adding a new configuration file is sufficient; no registration needed here.
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
