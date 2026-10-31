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

// A notification was stored: its recipient's open tabs are sent it.
public sealed record NotificationCreated(Guid NotificationId, Guid UserId) : IOutboxEvent
{
    public const string Name = nameof(NotificationCreated);

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

// A member's read position moved: the conversation is told how far they have read.
public sealed record ConversationRead(Guid ConversationId, Guid UserId, DateTime ReadAt) : IOutboxEvent
{
    public const string Name = nameof(ConversationRead);

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
        NotificationCreated.Name => Deserialize<NotificationCreated>(payload),
        ConversationCreated.Name => Deserialize<ConversationCreated>(payload),
        ConversationRead.Name => Deserialize<ConversationRead>(payload),
        _ => throw new InvalidOperationException($"The outbox has no event of kind '{kind}'."),
    };

    private static T Deserialize<T>(string payload) where T : IOutboxEvent
        => JsonSerializer.Deserialize<T>(payload, Json)
           ?? throw new InvalidOperationException("The outbox payload is empty.");
}
