using Microsoft.AspNetCore.Identity;

namespace ChatNET.API.Auth.Models;

// Extends IdentityUser<Guid> which already provides Id, UserName, Email,
// PasswordHash, and security-related columns. We add only the app-specific fields.
public class AppUser : IdentityUser<Guid>
{
    // Human-readable name shown in the UI. Not unique — two users can share a display name.
    // UserName (from Identity) is the unique login handle used for @mentions.
    public string DisplayName { get; set; } = string.Empty;

    public string? AvatarUrl { get; set; }

    // Updated on SignalR disconnect, not on every heartbeat. Answers "last seen when?"
    // Redis presence keys answer the separate question "is this user online right now?"
    public DateTime? LastSeenAt { get; set; }

    public bool IsDeactivated { get; set; }
    public DateTime? DeactivatedAt { get; set; }
}
