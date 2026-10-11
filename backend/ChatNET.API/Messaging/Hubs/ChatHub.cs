using System.Collections.Concurrent;
using ChatNET.API.Common.Auth;
using ChatNET.API.Conversations.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ChatNET.API.Messaging.Hubs;

// A new ChatHub instance is created for every incoming call, so it must not hold state in
// fields. State that has to outlive a call lives in the database, in Redis, in SignalR's own group
// tracking, or, for facts about one connection, in Context.Items (a dictionary that lives exactly
// as long as the connection).
//
// Context.User exposes the JWT claims, including "display_name".
// Context.ConnectionId identifies this one connection; a user with two tabs has two.
[Authorize]
public class ChatHub : Hub<IChatClient>
{
    // Sent to the client verbatim (a HubException message is the only error text SignalR forwards).
    // It is deliberately the same for a conversation that does not exist and one the user is not
    // in, so the hub cannot be used to find out which conversation IDs are real.
    private const string NotAMember = "You are not a member of this conversation.";

    private const string JoinedKey = "joined-conversations";

    private readonly IConversationRepository _conversations;
    private readonly ILogger<ChatHub> _logger;

    public ChatHub(IConversationRepository conversations, ILogger<ChatHub> logger)
    {
        _conversations = conversations;
        _logger = logger;
    }

    // Runs once per connection, including every reconnect. SignalR group membership is not stored
    // anywhere and is lost when a connection closes, so each new connection must be put into the
    // group of every conversation its user is an active member of.
    public override async Task OnConnectedAsync()
    {
        var userId = Context.User!.GetUserId();

        var conversationIds = await _conversations.GetActiveConversationIdsForUserAsync(
            userId, Context.ConnectionAborted);

        // In parallel: a user with many conversations should not wait for one Redis round trip
        // per group. The multiplexer pipelines the commands.
        await Task.WhenAll(conversationIds.Select(JoinGroupAsync));

        _logger.LogInformation(
            "Connection {ConnectionId} of user {UserId} joined {ConversationCount} conversations",
            Context.ConnectionId, userId, conversationIds.Count);

        await base.OnConnectedAsync();
    }

    // Client-callable. Adds this connection to a conversation's group, for conversations the user
    // became a member of while already connected. Group names are guessable, so membership is
    // checked first: without the check any user could listen to any conversation.
    public async Task JoinConversation(Guid conversationId)
    {
        var userId = Context.User!.GetUserId();

        if (!await _conversations.IsActiveMemberAsync(conversationId, userId, Context.ConnectionAborted))
        {
            _logger.LogWarning(
                "User {UserId} tried to join conversation {ConversationId} without being an active member",
                userId, conversationId);

            throw new HubException(NotAMember);
        }

        await JoinGroupAsync(conversationId);
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

    // Adding the same connection to the same group twice is harmless: a group is a set.
    private async Task JoinGroupAsync(Guid conversationId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, ChatGroups.Conversation(conversationId), Context.ConnectionAborted);

        JoinedConversations()[conversationId] = 0;
    }

    // The conversations this connection has actually been put into. It is the authority for what
    // the connection may do later (such as sending typing events) without asking the database each
    // time. A concurrent dictionary used as a set, because joins run in parallel.
    private ConcurrentDictionary<Guid, byte> JoinedConversations()
    {
        if (Context.Items.TryGetValue(JoinedKey, out var existing))
            return (ConcurrentDictionary<Guid, byte>)existing!;

        var created = new ConcurrentDictionary<Guid, byte>();
        Context.Items[JoinedKey] = created;
        return created;
    }
}
