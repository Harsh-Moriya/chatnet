using ChatNET.API.Presence;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace ChatNET.API.Messaging.Hubs;

// Takes a person's open connections out of a conversation when they stop being a member of it. Joining a conversation's
// SignalR group is a thing a connection does once, when it opens, so someone who is removed would go on receiving what is said
// in a group they are no longer in, and could go on typing into it, until their next reconnect.
//
// Their connections can be on any server instance, and an instance can only act on its own. So one instance publishes a
// message on a Redis channel (the one thing all instances share besides the database), every instance receives it,
// the publisher included, and each takes the connections it holds out of the group and out of the typing check. The message
// is what the outbox row for a person leaving does, so a failed publish is retried until it works.
public sealed class ConnectionEvictor
{
    // Not under the prefix SignalR's own channels use: this is a message between our servers, not between SignalR's.
    public static readonly RedisChannel Channel = RedisChannel.Literal("chatnet:connections:evict");

    private readonly RedisConnection _redis;
    private readonly ConnectionRegistry _registry;
    private readonly IHubContext<ChatHub, IChatClient> _hub;
    private readonly ILogger<ConnectionEvictor> _logger;
    private long _handled;

    public ConnectionEvictor(
        RedisConnection redis,
        ConnectionRegistry registry,
        IHubContext<ChatHub, IChatClient> hub,
        ILogger<ConnectionEvictor> logger)
    {
        _redis = redis;
        _registry = registry;
        _hub = hub;
        _logger = logger;
    }

    // How many messages this instance has finished acting on. A test that evicts someone waits for it to move, since the
    // instance hears the message a moment after it was published.
    public long EvictionsHandled => Interlocked.Read(ref _handled);

    // Asks every instance to take this person's connections out of this conversation. Repeating it is harmless.
    public async Task EvictAsync(Guid userId, Guid conversationId)
    {
        var subscriber = await _redis.GetSubscriberAsync();
        await subscriber.PublishAsync(Channel, Encode(userId, conversationId));
    }

    // What an instance does when it hears the message: for each connection of the person that it holds, forget the
    // conversation (so the connection can no longer type in it) and leave the group (so it no longer receives it).
    public async Task EvictLocalAsync(Guid userId, Guid conversationId)
    {
        try
        {
            var connections = _registry.Of(userId);

            foreach (var connection in connections)
            {
                ConnectionState.Forget(connection.Items, conversationId);
                await _hub.Groups.RemoveFromGroupAsync(connection.ConnectionId, ChatGroups.Conversation(conversationId));
            }

            _logger.LogInformation(
                "Took {ConnectionCount} connections of user {UserId} out of conversation {ConversationId}",
                connections.Count, userId, conversationId);
        }
        finally
        {
            Interlocked.Increment(ref _handled);
        }
    }

    public static string Encode(Guid userId, Guid conversationId) => $"{userId}:{conversationId}";

    public static bool TryDecode(string? message, out Guid userId, out Guid conversationId)
    {
        userId = conversationId = Guid.Empty;
        var parts = message?.Split(':');

        return parts is { Length: 2 } && Guid.TryParse(parts[0], out userId) && Guid.TryParse(parts[1], out conversationId);
    }
}
