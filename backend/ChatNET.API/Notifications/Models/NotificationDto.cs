using ChatNET.API.Messaging.Models;

namespace ChatNET.API.Notifications.Models;

// A notification as a client receives it, in the list and in the push. ActorDisplayName is the person who mentioned or added the
// recipient. MessageId and Message are only set for a Mention, and Message is null again if the message was deleted. The
// conversation's name is not here: a client already has it, in the form it is shown to the recipient.
public record NotificationDto(
    Guid Id,
    NotificationType Type,
    Guid? ConversationId,
    Guid ActorId,
    string ActorDisplayName,
    Guid? MessageId,
    NotificationMessageDto? Message,
    bool IsRead,
    DateTime CreatedAt
);

// The message a mention is about, as it was sealed: the server cannot read it, so the client opens it with the key of KeyEpoch to show
// the start of what was said. The person who sealed it is the actor of the notification.
public record NotificationMessageDto(string Ciphertext, int KeyEpoch, Guid ClientMessageId, int EditCount)
{
    // Null for a deleted message, which has nothing left to open.
    public static NotificationMessageDto? Of(MessageDto message) =>
        message.Ciphertext is null ? null : new NotificationMessageDto(message.Ciphertext, message.KeyEpoch, message.ClientMessageId, message.EditCount);
}
