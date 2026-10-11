using ChatNET.API.Messaging.Hubs;
using ChatNET.API.Messaging.Models;
using ChatNET.API.Conversations.Models;
using Microsoft.AspNetCore.SignalR;

namespace ChatNET.API.Tests.Messaging;

// A hub context whose pushes always fail, to simulate Redis or SignalR being unavailable at the
// moment a request tries to notify clients. Only the members the code under test uses are
// implemented; the rest throw, so an unexpected use shows up as a test failure.
public sealed class ThrowingHubContext : IHubContext<ChatHub, IChatClient>
{
    public IHubClients<IChatClient> Clients { get; } = new ThrowingClients();

    public IGroupManager Groups => throw new NotSupportedException();

    private sealed class ThrowingClients : IHubClients<IChatClient>
    {
        private static readonly IChatClient Client = new ThrowingChatClient();

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
        private static InvalidOperationException Failure() => new("Simulated push failure.");

        public Task ReceiveMessage(MessageDto message) => throw Failure();

        public Task UserTyping(Guid conversationId, Guid userId, string displayName, bool isTyping) => throw Failure();

        public Task AddedToConversation(ConversationDto conversation) => throw Failure();
    }
}
