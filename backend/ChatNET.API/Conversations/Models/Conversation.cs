using ChatNET.API.Auth.Models;

namespace ChatNET.API.Conversations.Models;

public class Conversation
{
    public Guid Id { get; set; }
    public ConversationType Type { get; set; }

    // Null for Direct conversations
    public string? Name { get; set; }

    // The key of the group's picture in the bucket (see AvatarKeys), or null. Always null for a direct chat, which shows
    // the other person's picture.
    public string? AvatarKey { get; set; }

    // Composite of the two participant IDs sorted ascending: "{smallerId}:{largerId}".
    // The partial unique index on this column prevents duplicate DM conversations.
    // Generated in C# (so it can be unit tested) and enforced by a DB check constraint.
    public string? DirectKey { get; set; }

    public DateTime CreatedAt { get; set; }

    // Updated on every new message. Drives the sidebar sort order (most active first)
    // without requiring a subquery against the Messages table on every sidebar load.
    public DateTime UpdatedAt { get; set; }

    public Guid CreatedById { get; set; }

    public AppUser CreatedBy { get; set; } = null!;
    public ICollection<ConversationMember> Members { get; set; } = [];
}
