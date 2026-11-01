using ChatNET.API.Conversations.Models;
using ChatNET.API.Messaging.Hubs;
using ChatNET.API.Messaging.Models;
using Microsoft.AspNetCore.SignalR;

namespace ChatNET.API.Tests.Messaging;

// A hub context that runs a callback at the moment a message is pushed, instead of delivering it.
// It lets a test look at the state of the system at that exact instant, for example to check that
// the message is already in the database. Only the members the code under test uses are
// implemented; the rest throw, so an unexpected use shows up as a test failure.
public sealed class SpyHubContext : IHubContext<ChatHub, IChatClient>
{
    public SpyHubContext(Func<string, MessageDto, Task> onReceiveMessage)
    {
        Clients = new SpyClients(onReceiveMessage);
    }

    public IHubClients<IChatClient> Clients { get; }

    public IGroupManager Groups => throw new NotSupportedException();

    private sealed class SpyClients : IHubClients<IChatClient>
    {
        private readonly Func<string, MessageDto, Task> _onReceiveMessage;

        public SpyClients(Func<string, MessageDto, Task> onReceiveMessage)
        {
            _onReceiveMessage = onReceiveMessage;
        }

        public IChatClient Group(string groupName) => new SpyChatClient(groupName, _onReceiveMessage);

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

    private sealed class SpyChatClient : IChatClient
    {
        private readonly string _group;
        private readonly Func<string, MessageDto, Task> _onReceiveMessage;

        public SpyChatClient(string group, Func<string, MessageDto, Task> onReceiveMessage)
        {
            _group = group;
            _onReceiveMessage = onReceiveMessage;
        }

        public Task ReceiveMessage(MessageDto message) => _onReceiveMessage(_group, message);

        public Task MessageUpdated(MessageDto message) => throw new NotSupportedException();

        public Task UserTyping(Guid conversationId, Guid userId, string displayName, bool isTyping) =>
            throw new NotSupportedException();

        public Task AddedToConversation(ConversationDto conversation) => throw new NotSupportedException();

        public Task MessageRead(Guid conversationId, Guid userId, DateTime readAt) => throw new NotSupportedException();

        public Task PresenceChanged(ChatNET.API.Presence.PresenceDto presence) => throw new NotSupportedException();

        public Task NotificationReceived(ChatNET.API.Notifications.Models.NotificationDto notification) =>
            throw new NotSupportedException();

        public Task NotificationsRead(Guid[] ids) => throw new NotSupportedException();

        public Task NotificationDismissed(Guid id) => throw new NotSupportedException();

        public Task ConversationMuteChanged(Guid conversationId, bool isMuted) => throw new NotSupportedException();

        public Task MembersChanged(MembersChangedDto change) => throw new NotSupportedException();

        public Task ConversationUpdated(ConversationUpdateDto update) => throw new NotSupportedException();

        public Task RemovedFromConversation(Guid conversationId, DateTime leftAt) => throw new NotSupportedException();
    }
}
