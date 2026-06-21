using ChatNET.API.Auth.Models;
using ChatNET.API.Conversations.Models;

namespace ChatNET.API.Messaging.Models;

public class Message
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid SenderId { get; set; }

    // Nullable: set to null when the message is deleted. This removes the content
    // while preserving the message row so the timeline stays intact (no gaps).
    public string? Content { get; set; }

    public DateTime CreatedAt { get; set; }
    public int EditCount { get; set; }
    public DateTime? EditedAt { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }

    // May differ from SenderId when an admin deletes another user's message.
    public Guid? DeletedById { get; set; }

    public Conversation Conversation { get; set; } = null!;
    public AppUser Sender { get; set; } = null!;
    public AppUser? DeletedBy { get; set; }
    public ICollection<MessageAttachment> Attachments { get; set; } = [];
}
