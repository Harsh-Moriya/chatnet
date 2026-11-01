using ChatNET.API.Messaging.Hubs;
using ChatNET.API.Messaging.Models;
using ChatNET.API.Conversations.Models;
using Microsoft.AspNetCore.SignalR;

namespace ChatNET.API.Tests.Messaging;

// A hub context whose pushes always fail, to simulate Redis or SignalR being unavailable at the
// moment a request tries to notify clients. It throws an InvalidOperationException unless it is given another exception
// to throw (a timeout that surfaces as a cancellation, for example). Only the members the code under test uses are
// implemented; the rest throw, so an unexpected use shows up as a test failure.
public sealed class ThrowingHubContext : IHubContext<ChatHub, IChatClient>
{
    public ThrowingHubContext(Func<Exception>? failure = null)
    {
        Clients = new ThrowingClients(failure ?? (() => new InvalidOperationException("Simulated push failure.")));
    }

    public IHubClients<IChatClient> Clients { get; }

    public IGroupManager Groups => throw new NotSupportedException();

    private sealed class ThrowingClients : IHubClients<IChatClient>
    {
        private readonly IChatClient Client;

        public ThrowingClients(Func<Exception> failure)
        {
            Client = new ThrowingChatClient(failure);
        }

        public IChatClient User(string userId) => Client;

        public IChatClient Users(IReadOnlyList<string> userIds) => Client;

        public IChatClient Group(string groupName) => Client;

        public IChatClient All => Client;

        public IChatClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => Client;

        IChatClient IHubClients<IChatClient>.Client(string connectionId) => Client;

        public IChatClient Clients(IReadOnlyList<string> connectionIds) => Client;

        public IChatClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Client;

        public IChatClient Groups(IReadOnlyList<string> groupNames) => Client;
    }

    private sealed class ThrowingChatClient : IChatClient
    {
        private readonly Func<Exception> _failure;

        public ThrowingChatClient(Func<Exception> failure)
        {
            _failure = failure;
        }

        private Exception Failure() => _failure();

        public Task ReceiveMessage(MessageDto message) => throw Failure();

        public Task MessageUpdated(MessageDto message) => throw Failure();

        public Task UserTyping(Guid conversationId, Guid userId, string displayName, bool isTyping) => throw Failure();

        public Task AddedToConversation(ConversationDto conversation) => throw Failure();

        public Task MessageRead(Guid conversationId, Guid userId, DateTime readAt) => throw Failure();

        public Task PresenceChanged(ChatNET.API.Presence.PresenceDto presence) => throw Failure();

        public Task NotificationReceived(ChatNET.API.Notifications.Models.NotificationDto notification) => throw Failure();

        public Task NotificationsRead(Guid[] ids) => throw Failure();

        public Task NotificationDismissed(Guid id) => throw Failure();

        public Task ConversationMuteChanged(Guid conversationId, bool isMuted) => throw Failure();

        public Task MembersChanged(MembersChangedDto change) => throw Failure();

        public Task ConversationUpdated(ConversationUpdateDto update) => throw Failure();

        public Task RemovedFromConversation(Guid conversationId, DateTime leftAt) => throw Failure();
    }
}
