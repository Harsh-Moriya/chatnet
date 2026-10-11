using ChatNET.API.Conversations.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ChatNET.API.Messaging.Hubs;

// A new ChatHub instance is created for every incoming call, so it must not hold state
// in fields. Anything that has to outlive a call lives in the database, in Redis, or in
// SignalR's own group and user tracking.
//
// Context.UserIdentifier is the caller's user ID as a string.
// Context.User exposes the JWT claims, including "display_name".
// Context.ConnectionId identifies this one connection; a user with two tabs has two.
[Authorize]
public class ChatHub : Hub<IChatClient>
{
    private readonly IConversationRepository _conversations;

    public ChatHub(IConversationRepository conversations)
    {
        _conversations = conversations;
    }

    // Runs once per connection, including every reconnect. Group membership is not
    // persisted anywhere, so this is where a connection is put into the group of every
    // conversation its user is an active member of.
    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
    }

    // Runs when a connection closes. exception is null for a clean close.
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await base.OnDisconnectedAsync(exception);
    }

    // Client-callable. Adds this connection to a conversation's group. Group names are
    // guessable, so membership must be verified before joining or any user could listen
    // to any conversation. A failed check should throw HubException, which is the only
    // exception type whose message SignalR sends back to the client.
    public Task JoinConversation(Guid conversationId)
    {
        return Task.CompletedTask;
    }

    // Client-callable. Tells the other members of the conversation this user is typing.
    // Typing state is ephemeral, so this touches neither the database nor MediatR.
    public Task StartTyping(Guid conversationId)
    {
        return Task.CompletedTask;
    }

    public Task StopTyping(Guid conversationId)
    {
        return Task.CompletedTask;
    }
}
