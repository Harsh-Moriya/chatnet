namespace ChatNET.API.Notifications.Models;

// A notification as a client receives it, in the list and in the push. ActorDisplayName is the person who
// mentioned or added the recipient. MessageId and MessagePreview are only set for a Mention, and the preview
// is null again if the message was deleted. The conversation's name is not here: a client already has it, in
// the form it is shown to the recipient.
public record NotificationDto(
    Guid Id,
    NotificationType Type,
    Guid? ConversationId,
    Guid ActorId,
    string ActorDisplayName,
    Guid? MessageId,
    string? MessagePreview,
    bool IsRead,
    DateTime CreatedAt
);
