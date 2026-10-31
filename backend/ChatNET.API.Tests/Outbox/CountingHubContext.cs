using System.Collections.Concurrent;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Messaging.Hubs;
using ChatNET.API.Messaging.Models;
using ChatNET.API.Notifications.Models;
using ChatNET.API.Presence;
using Microsoft.AspNetCore.SignalR;

namespace ChatNET.API.Tests.Outbox;

// A hub context that only records the read positions pushed to each group, in the order they were pushed. Two hosts
// given the same instance share the record, which is how a test sees whether two relays pushed one row twice. Only the
// members the code under test uses are implemented; the rest throw, so an unexpected use shows up as a test failure.
public sealed class CountingHubContext : IHubContext<ChatHub, IChatClient>
{
    private readonly ConcurrentQueue<(string Group, Guid ConversationId, Guid UserId, DateTime ReadAt)> _reads = new();

    public CountingHubContext()
    {
        Clients = new CountingClients(_reads);
    }

    public IReadOnlyList<(string Group, Guid ConversationId, Guid UserId, DateTime ReadAt)> Reads => _reads.ToArray();

    public IHubClients<IChatClient> Clients { get; }

    public IGroupManager Groups => throw new NotSupportedException();

    private sealed class CountingClients : IHubClients<IChatClient>
    {
        private readonly ConcurrentQueue<(string, Guid, Guid, DateTime)> _reads;

        public CountingClients(ConcurrentQueue<(string, Guid, Guid, DateTime)> reads)
        {
            _reads = reads;
        }

        public IChatClient Group(string groupName) => new CountingClient(groupName, _reads);

        public IChatClient User(string userId) => throw new NotSupportedException();

        public IChatClient Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();

        public IChatClient All => throw new NotSupportedException();

        public IChatClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();

        IChatClient IHubClients<IChatClient>.Client(string connectionId) => throw new NotSupportedException();

        public IChatClient Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();

        public IChatClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) =>
            throw new NotSupportedException();

        public IChatClient Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();
    }

    private sealed class CountingClient : IChatClient
    {
        private readonly string _group;
        private readonly ConcurrentQueue<(string, Guid, Guid, DateTime)> _reads;

        public CountingClient(string group, ConcurrentQueue<(string, Guid, Guid, DateTime)> reads)
        {
            _group = group;
            _reads = reads;
        }

        public Task MessageRead(Guid conversationId, Guid userId, DateTime readAt)
        {
            _reads.Enqueue((_group, conversationId, userId, readAt));
            return Task.CompletedTask;
        }

        public Task ReceiveMessage(MessageDto message) => throw new NotSupportedException();

        public Task MessageUpdated(MessageDto message) => throw new NotSupportedException();

        public Task UserTyping(Guid conversationId, Guid userId, string displayName, bool isTyping) =>
            throw new NotSupportedException();

        public Task AddedToConversation(ConversationDto conversation) => throw new NotSupportedException();

        public Task PresenceChanged(PresenceDto presence) => throw new NotSupportedException();

        public Task NotificationReceived(NotificationDto notification) => throw new NotSupportedException();

        public Task NotificationsRead(Guid[] ids) => throw new NotSupportedException();

        public Task NotificationDismissed(Guid id) => throw new NotSupportedException();

        public Task ConversationMuteChanged(Guid conversationId, bool isMuted) => throw new NotSupportedException();
    }
}
