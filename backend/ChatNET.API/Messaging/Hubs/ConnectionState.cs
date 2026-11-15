using System.Collections.Concurrent;

namespace ChatNET.API.Messaging.Hubs;

// The facts about one connection that live in its Items dictionary (which lives exactly as long as the connection): the
// conversations it was put into, and when it last had a typing event delivered in each. Kept in one place because the hub
// reads and writes them for its own connection, and the eviction (ConnectionEvictor) edits them from outside the hub when a
// person stops being a member.
public static class ConnectionState
{
    private const string JoinedKey = "joined-conversations";
    private const string TypingKey = "typing-started-at";
    private const string CallKey = "call-id";

    // The conversations this connection has actually been put into. It is the authority for what the connection may do
    // later (such as sending typing events) without asking the database each time. A concurrent dictionary used as a set,
    // because joins run in parallel.
    public static ConcurrentDictionary<Guid, byte> Joined(IDictionary<object, object?> items)
        => OfKind<Guid, byte>(items, JoinedKey);

    // When this connection last had a "typing" event delivered, per conversation (a monotonic millisecond clock, so a
    // change of system time cannot affect it). Bounded by the number of conversations the connection joined.
    public static ConcurrentDictionary<Guid, long> TypingStartedAt(IDictionary<object, object?> items)
        => OfKind<Guid, long>(items, TypingKey);

    // The call this connection is a side of, if any. Set when it starts, answers or rejoins a call, and cleared when the call is found to be
    // over; the call service renews its lease and detaches it when the connection closes.
    public static Guid? CallOf(IDictionary<object, object?> items)
        => items.TryGetValue(CallKey, out var id) ? (Guid?)id : null;

    public static void SetCall(IDictionary<object, object?> items, Guid? callId)
    {
        if (callId is { } id)
            items[CallKey] = id;
        else
            items.Remove(CallKey);
    }

    // The connection no longer belongs in this conversation: it may not type in it, and the next typing event is not
    // held back by what was delivered before.
    public static void Forget(IDictionary<object, object?> items, Guid conversationId)
    {
        Joined(items).TryRemove(conversationId, out _);
        TypingStartedAt(items).TryRemove(conversationId, out _);
    }

    private static ConcurrentDictionary<TKey, TValue> OfKind<TKey, TValue>(IDictionary<object, object?> items, string key)
        where TKey : notnull
    {
        if (items.TryGetValue(key, out var existing))
            return (ConcurrentDictionary<TKey, TValue>)existing!;

        var created = new ConcurrentDictionary<TKey, TValue>();
        items[key] = created;
        return created;
    }
}
