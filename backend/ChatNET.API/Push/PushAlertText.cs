using ChatNET.API.Calls.Models;
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

    // A call that nobody answered, from the caller. The tag is the call's, so an alert that the call is ringing (and whatever else the
    // call showed) is replaced by this one on the screen.
    public static PushAlert ForMissedCall(MessageDto message, CallDto call) => new(
        message.SenderDisplayName,
        call.Media == CallMedia.Video ? "Missed video call" : "Missed audio call",
        CallTag(call.Id),
        PushAlertTypes.MissedCall,
        message.ConversationId,
        message.Id);

    public static string CallTag(Guid callId) => $"call-{callId}";

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
