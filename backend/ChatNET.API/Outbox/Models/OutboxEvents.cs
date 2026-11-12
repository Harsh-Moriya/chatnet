using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatNET.API.Outbox.Models;

// What can be in the outbox. Each event names the thing that changed and is small on purpose: the push itself is
// built from the stored data when it is made (see OutboxDispatcher).
public interface IOutboxEvent
{
    // The column value. Also the name used to read the payload back.
    [JsonIgnore]
    string Kind { get; }
}

// A message was stored: everyone in the conversation is sent it.
public sealed record MessageCreated(Guid MessageId, Guid ConversationId) : IOutboxEvent
{
    public const string Name = nameof(MessageCreated);

    [JsonIgnore]
    public string Kind => Name;
}

// A message already stored was edited or deleted: everyone in the conversation is sent it as it is now.
public sealed record MessageUpdated(Guid MessageId, Guid ConversationId) : IOutboxEvent
{
    public const string Name = nameof(MessageUpdated);

    [JsonIgnore]
    public string Kind => Name;
}

// A message was deleted and had files: they are removed from the bucket. The only event that is not a push; it is here
// because a failure to remove a file has to be retried, and a file that is never removed is a file nobody can reach
// and nobody can account for.
public sealed record MessageFilesRemoved(Guid MessageId) : IOutboxEvent
{
    public const string Name = nameof(MessageFilesRemoved);

    [JsonIgnore]
    public string Kind => Name;
}

// The object of a file that belonged to a message that no longer exists is taken out of the bucket. Like MessageFilesRemoved it is not
// a push. It names the key and not the message, because the rows are made by SQL at the moment the messages are removed, when the
// files can no longer be found by their message. The key is a path made by the server (see AttachmentKeys), not content.
public sealed record StoredFileRemoved(string Key) : IOutboxEvent
{
    public const string Name = nameof(StoredFileRemoved);

    [JsonIgnore]
    public string Kind => Name;
}

// A notification was stored: its recipient's open tabs are sent it.
public sealed record NotificationCreated(Guid NotificationId, Guid UserId) : IOutboxEvent
{
    public const string Name = nameof(NotificationCreated);

    [JsonIgnore]
    public string Kind => Name;
}

// A message was stored: the members who are away from the application (no live connection) and have allowed alerts are alerted by
// push. Not the same row as MessageCreated, which the request that stored the message pushes itself: this one is for the relay,
// which looks at who is away when it takes the row. The people the message names are in it, because they are alerted by the mention
// and not by the message, and that must not change if the mention is read or dismissed before the relay gets here.
public sealed record MessageAlertRequested(
    Guid MessageId, Guid ConversationId, IReadOnlyList<Guid> MentionedUserIds) : IOutboxEvent
{
    public const string Name = nameof(MessageAlertRequested);

    [JsonIgnore]
    public string Kind => Name;
}

// A notification (a mention, or being added to a group) was stored: its recipient is alerted by push if they are away.
public sealed record NotificationAlertRequested(Guid NotificationId, Guid UserId) : IOutboxEvent
{
    public const string Name = nameof(NotificationAlertRequested);

    [JsonIgnore]
    public string Kind => Name;
}

// A conversation was stored: its members' open connections are told, which is how they learn to join its group.
public sealed record ConversationCreated(
    Guid ConversationId, Guid CreatorId, IReadOnlyList<Guid> MemberIds) : IOutboxEvent
{
    public const string Name = nameof(ConversationCreated);

    [JsonIgnore]
    public string Kind => Name;
}

// The members of a group changed (someone joined, left, or has another role): the people in it are told how those entries
// are now.
public sealed record MembersChanged(Guid ConversationId, IReadOnlyList<Guid> UserIds) : IOutboxEvent
{
    public const string Name = nameof(MembersChanged);

    [JsonIgnore]
    public string Kind => Name;
}

// People were added to a group, or came back to it: each is sent the conversation, which is how their open connections learn
// to join its group.
public sealed record MembersJoined(Guid ConversationId, IReadOnlyList<Guid> UserIds) : IOutboxEvent
{
    public const string Name = nameof(MembersJoined);

    [JsonIgnore]
    public string Kind => Name;
}

// Someone left a group, or was removed: their tabs are told, and every connection of theirs leaves the group's SignalR group,
// so they hear nothing more that is said in it.
public sealed record MemberLeft(Guid ConversationId, Guid UserId) : IOutboxEvent
{
    public const string Name = nameof(MemberLeft);

    [JsonIgnore]
    public string Kind => Name;
}

// The keys of a conversation changed (a new epoch was stored, or a member left and the current one must be replaced): the people in
// it are told which epoch is newest and whether it must be replaced, as it is when the push is made.
public sealed record ConversationKeyChanged(Guid ConversationId) : IOutboxEvent
{
    public const string Name = nameof(ConversationKeyChanged);

    [JsonIgnore]
    public string Kind => Name;
}

// A person changed their name or picture: the people who share a conversation with them, and their own tabs, are told how
// they are now.
public sealed record ProfileChanged(Guid UserId) : IOutboxEvent
{
    public const string Name = nameof(ProfileChanged);

    [JsonIgnore]
    public string Kind => Name;
}

// A picture was replaced or removed: the object it was is taken out of the bucket. Like MessageFilesRemoved it is not a push;
// it is here because a failure to remove an object has to be retried, and a picture that is never removed stays readable by
// anyone who kept its address. The key is a path made by the server (see AvatarKeys), not content.
public sealed record AvatarFileRemoved(string Key) : IOutboxEvent
{
    public const string Name = nameof(AvatarFileRemoved);

    [JsonIgnore]
    public string Kind => Name;
}

// A group was renamed, or its picture changed: the people in it are told the name and picture it has now.
public sealed record ConversationRenamed(Guid ConversationId) : IOutboxEvent
{
    public const string Name = nameof(ConversationRenamed);

    [JsonIgnore]
    public string Kind => Name;
}

// A member's read position moved: the conversation is told how far they have read.
public sealed record ConversationRead(Guid ConversationId, Guid UserId, DateTime ReadAt) : IOutboxEvent
{
    public const string Name = nameof(ConversationRead);

    [JsonIgnore]
    public string Kind => Name;
}

// Someone asked for a password reset link. The person is named only when the address belongs to an account that may be sent one:
// for an address nobody has, a deactivated account, or a request over the per-address limit it is null, and the relay does nothing
// for the row. The row exists in every case so a request costs the same whichever it was, and an answer that came back faster
// for a real account would tell a stranger which addresses have one. The token is not in the row: it is made, and stored hashed,
// when the relay sends the email, so the outbox table never holds a working link.
public sealed record PasswordResetRequested(Guid? UserId) : IOutboxEvent
{
    public const string Name = nameof(PasswordResetRequested);

    [JsonIgnore]
    public string Kind => Name;
}

public static class OutboxEvents
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Serialize(IOutboxEvent outboxEvent)
        => JsonSerializer.Serialize(outboxEvent, outboxEvent.GetType(), Json);

    public static IOutboxEvent Read(string kind, string payload) => kind switch
    {
        MessageCreated.Name => Deserialize<MessageCreated>(payload),
        MessageUpdated.Name => Deserialize<MessageUpdated>(payload),
        MessageFilesRemoved.Name => Deserialize<MessageFilesRemoved>(payload),
        StoredFileRemoved.Name => Deserialize<StoredFileRemoved>(payload),
        NotificationCreated.Name => Deserialize<NotificationCreated>(payload),
        MessageAlertRequested.Name => Deserialize<MessageAlertRequested>(payload),
        NotificationAlertRequested.Name => Deserialize<NotificationAlertRequested>(payload),
        ConversationCreated.Name => Deserialize<ConversationCreated>(payload),
        ConversationRead.Name => Deserialize<ConversationRead>(payload),
        MembersChanged.Name => Deserialize<MembersChanged>(payload),
        MembersJoined.Name => Deserialize<MembersJoined>(payload),
        MemberLeft.Name => Deserialize<MemberLeft>(payload),
        ConversationRenamed.Name => Deserialize<ConversationRenamed>(payload),
        ConversationKeyChanged.Name => Deserialize<ConversationKeyChanged>(payload),
        ProfileChanged.Name => Deserialize<ProfileChanged>(payload),
        AvatarFileRemoved.Name => Deserialize<AvatarFileRemoved>(payload),
        PasswordResetRequested.Name => Deserialize<PasswordResetRequested>(payload),
        _ => throw new InvalidOperationException($"The outbox has no event of kind '{kind}'."),
    };

    private static T Deserialize<T>(string payload) where T : IOutboxEvent
        => JsonSerializer.Deserialize<T>(payload, Json)
           ?? throw new InvalidOperationException("The outbox payload is empty.");
}
