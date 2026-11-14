namespace ChatNET.API.Push;

// What the browser is told, and shows: the text of the alert and where it leads. The ids are all it needs to take the person
// to the conversation (and for a mention, the message). The text says who and what kind (a new message, a file, a mention) and
// never the message itself, which the server cannot read; it is encrypted to the browser on the way, so the push service relays
// what it cannot read.
public sealed record PushAlert(
    string Title,
    string Body,
    // Alerts with the same tag replace each other on the screen, so a conversation shows one at a time.
    string Tag,
    string Type,
    Guid ConversationId,
    Guid? MessageId);

public static class PushAlertTypes
{
    public const string Message = "message";
    public const string Mention = "mention";
    public const string Invitation = "invitation";
}
