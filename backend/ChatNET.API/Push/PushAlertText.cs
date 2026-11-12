using ChatNET.API.Messaging.Models;
using ChatNET.API.Notifications.Models;

namespace ChatNET.API.Push;

// The words of an alert that is pushed to a browser that is away. The server cannot read what was said, and the alert would pass
// through the push service, so it says who sent it, in which group, and that there is a message or how many files it carries, and
// nothing of the text; a page that is open shows the text itself (alerts.ts), which it can open. A group is named in the title; in
// a direct conversation the sender's name is the title, because the name of the conversation is the sender's.
public static class PushAlertText
{
    public static string Tag(Guid conversationId) => $"conversation-{conversationId}";

    // A new message from someone else. "groupName" is null in a direct conversation.
    public static PushAlert ForMessage(MessageDto message, string? groupName, int fileCount)
    {
        var body = fileCount switch
        {
            0 => "New message",
            1 => "Sent a file",
            _ => $"Sent {fileCount} files",
        };

        return new PushAlert(
            groupName is null ? message.SenderDisplayName : $"{message.SenderDisplayName} in {groupName}",
            body,
            Tag(message.ConversationId),
            PushAlertTypes.Message,
            message.ConversationId,
            message.Id);
    }

    // Someone named the person in a message. It alerts in a muted conversation too. The title says it; there is no text.
    public static PushAlert ForMention(NotificationDto notification, Guid conversationId, string? groupName) => new(
        groupName is null
            ? $"{notification.ActorDisplayName} mentioned you"
            : $"{notification.ActorDisplayName} mentioned you in {groupName}",
        string.Empty,
        Tag(conversationId),
        PushAlertTypes.Mention,
        conversationId,
        notification.MessageId);

    public static PushAlert ForInvitation(NotificationDto notification, Guid conversationId, string? groupName) => new(
        $"{notification.ActorDisplayName} added you to {groupName ?? "a group"}",
        string.Empty,
        Tag(conversationId),
        PushAlertTypes.Invitation,
        conversationId,
        null);
}
