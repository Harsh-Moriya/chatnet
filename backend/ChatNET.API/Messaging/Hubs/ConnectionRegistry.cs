using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace ChatNET.API.Messaging.Hubs;

// The open connections of this server instance, by user. SignalR can send to a user, or to a group, but cannot say which
// connections a user has, and a hub instance lives for one call. Taking a person out of a conversation has to reach their
// connections wherever they are, and each server instance only holds its own, so each keeps this list and acts on it
// when it is told (ConnectionEvictor).
public sealed class ConnectionRegistry
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, HubCallerContext>> _byUser = new();

    public void Add(Guid userId, HubCallerContext context)
        => _byUser.GetOrAdd(userId, _ => new ConcurrentDictionary<string, HubCallerContext>())[context.ConnectionId] = context;

    // A user with no connections left is dropped, so the list does not grow with everyone who ever connected.
    public void Remove(Guid userId, string connectionId)
    {
        if (!_byUser.TryGetValue(userId, out var connections))
            return;

        connections.TryRemove(connectionId, out _);

        if (connections.IsEmpty)
            _byUser.TryRemove(new KeyValuePair<Guid, ConcurrentDictionary<string, HubCallerContext>>(userId, connections));
    }

    public IReadOnlyCollection<HubCallerContext> Of(Guid userId)
        => _byUser.TryGetValue(userId, out var connections) ? connections.Values.ToArray() : [];

    public int UserCount => _byUser.Count;
}
