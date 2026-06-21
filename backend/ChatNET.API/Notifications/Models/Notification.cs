using ChatNET.API.Auth.Models;
using ChatNET.API.Conversations.Models;

namespace ChatNET.API.Notifications.Models;

public class Notification
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    // Who triggered the notification — stored here so "John sent a message" can be
    // displayed without a separate query to look up the actor.
    public Guid ActorId { get; set; }

    public Guid? ConversationId { get; set; }
    public NotificationType Type { get; set; }
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; }

    // Recipient
    public AppUser User { get; set; } = null!;
    public AppUser Actor { get; set; } = null!;
    public Conversation? Conversation { get; set; }
}
