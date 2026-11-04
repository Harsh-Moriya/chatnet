using ChatNET.API.Messaging.Models;
using ChatNET.API.Notifications.Models;

namespace ChatNET.API.Push;

// The words of an alert, made the way alerts.ts makes them for the page, so an alert reads the same whether it came over the
// connection or as a push. A group is named in the title; in a direct conversation the sender's name is the title, because the
// name of the conversation is the sender's.
public static class PushAlertText
{
    public const int PreviewLength = 100;

    public static string Tag(Guid conversationId) => $"conversation-{conversationId}";

    // Trimmed, and cut to PreviewLength with an ellipsis. The cut never lands inside a surrogate pair, which would leave half
    // of a character.
    public static string Preview(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length <= PreviewLength)
            return trimmed;

        var length = char.IsHighSurrogate(trimmed[PreviewLength - 1]) ? PreviewLength - 1 : PreviewLength;
        return trimmed[..length].TrimEnd() + "…";
    }

    // A new message from someone else. "groupName" is null in a direct conversation.
    public static PushAlert ForMessage(MessageDto message, string? groupName, int fileCount)
    {
        string body;
        if (!string.IsNullOrWhiteSpace(message.Content))
            body = Preview(message.Content);
        else if (fileCount > 0)
            body = fileCount == 1 ? "Sent a file" : $"Sent {fileCount} files";
        else
            body = string.Empty;

        return new PushAlert(
            groupName is null ? message.SenderDisplayName : $"{message.SenderDisplayName} in {groupName}",
            body,
            Tag(message.ConversationId),
            PushAlertTypes.Message,
            message.ConversationId,
            message.Id);
    }

    // Someone named the person in a message. It alerts in a muted conversation too, and its text is the message's.
    public static PushAlert ForMention(NotificationDto notification, Guid conversationId, string? groupName) => new(
        groupName is null
            ? $"{notification.ActorDisplayName} mentioned you"
            : $"{notification.ActorDisplayName} mentioned you in {groupName}",
        string.IsNullOrWhiteSpace(notification.MessagePreview) ? string.Empty : Preview(notification.MessagePreview),
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
