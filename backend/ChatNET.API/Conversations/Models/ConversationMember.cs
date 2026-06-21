using ChatNET.API.Auth.Models;

namespace ChatNET.API.Conversations.Models;

public class ConversationMember
{
    public Guid ConversationId { get; set; }
    public Guid UserId { get; set; }
    public MemberRole Role { get; set; }

    // Lower bound for message history: members only see messages sent after they joined.
    public DateTime JoinedAt { get; set; }

    // Null means the member is still active. A timestamp captures when they left,
    // which is more useful than a boolean IsActive flag (it scopes history windows).
    public DateTime? LeftAt { get; set; }

    // Watermark for unread count. Unread = messages WHERE CreatedAt > LastReadAt.
    // This avoids a per-message read receipt row for every user in every conversation.
    public DateTime? LastReadAt { get; set; }

    public bool IsMuted { get; set; }

    public Conversation Conversation { get; set; } = null!;
    public AppUser User { get; set; } = null!;
}
