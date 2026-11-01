using ChatNET.API.Common.Auth;
using ChatNET.API.Conversations;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Presence;
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
    // The shortest time between two delivered "typing" events from one connection in one
    // conversation. A client should send one every couple of seconds while someone types.
    private static readonly TimeSpan TypingInterval = TimeSpan.FromSeconds(1);

    private readonly IConversationRepository _conversations;
    private readonly PresenceService _presence;
    private readonly ConnectionRegistry _registry;
    private readonly ILogger<ChatHub> _logger;

    public ChatHub(
        IConversationRepository conversations, PresenceService presence, ConnectionRegistry registry, ILogger<ChatHub> logger)
    {
        _conversations = conversations;
        _presence = presence;
        _registry = registry;
        _logger = logger;
    }

    // Runs once per connection, including every reconnect. SignalR group membership is not stored
    // anywhere and is lost when a connection closes, so each new connection must be put into the
    // group of every conversation its user is an active member of.
    public override async Task OnConnectedAsync()
    {
        var userId = Context.User!.GetUserId();

        // Known before anything is read, so a person who is taken out of a conversation from now on reaches this
        // connection (see ConnectionEvictor), whatever it has joined by then.
        _registry.Add(userId, Context);

        var conversationIds = await _conversations.GetActiveConversationIdsForUserAsync(
            userId, Context.ConnectionAborted);

        // In parallel: a user with many conversations should not wait for one Redis round trip
        // per group. The multiplexer pipelines the commands.
        await Task.WhenAll(conversationIds.Select(JoinGroupAsync));

        // A removal that committed while the list above was being read may have sent its message before this connection was
        // in the group, which leaves the connection in a group it does not belong in. Reading again, after the joins, sees
        // every removal that committed before it; one that commits after it sends its message to a connection that is
        // registered and in its groups by then.
        await LeaveConversationsNoLongerActiveAsync(userId, conversationIds);

        _logger.LogInformation(
            "Connection {ConnectionId} of user {UserId} joined {ConversationCount} conversations",
            Context.ConnectionId, userId, conversationIds.Count);

        // After the groups, so the connection is fully usable whatever happens to presence: recording it
        // cannot fail the connection (the service logs and drops its own failures).
        await _presence.ConnectedAsync(userId, Context.ConnectionId);

        await base.OnConnectedAsync();
    }

    // Runs when a connection closes, however it closed (the user left, the network dropped, the token
    // expired). A server that dies never gets here; its connections stop being renewed and expire.
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = Context.User!.GetUserId();
        _registry.Remove(userId, Context.ConnectionId);

        await _presence.DisconnectedAsync(userId, Context.ConnectionId);

        await base.OnDisconnectedAsync(exception);
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

            throw new HubException(ConversationErrors.NotAMember);
        }

        await JoinGroupAsync(conversationId);

        // The check above and the join are two steps, and a removal can happen between them, with its message arriving
        // before the join: so the connection would be in the group of a conversation its user has just left. Asking again
        // after the join closes that gap, for a removal after this check reaches the connection once it is in the group.
    }

    // Client-callable. Tells the other members of the conversation this user is typing.
    // Typing state is ephemeral, so this touches neither the database nor MediatR.
    //
    // The event is sent to everyone in the conversation's group, so the sender must really be in it:
    // otherwise anyone who knew a conversation ID could push "X is typing" into a private
    // conversation. The check uses the set of conversations this connection joined (no query).
    // Typing is also limited to one event per interval per conversation, so a misbehaving client
    // cannot turn each keystroke into a message to every member.
    public async Task StartTyping(Guid conversationId)
    {
        if (!ConnectionState.Joined(Context.Items).ContainsKey(conversationId))
            throw new HubException(ConversationErrors.NotAMember);

        var now = Environment.TickCount64;
        var lastSent = ConnectionState.TypingStartedAt(Context.Items);

        if (lastSent.TryGetValue(conversationId, out var previous) && now - previous < TypingInterval.TotalMilliseconds)
            return;

        lastSent[conversationId] = now;

        await Clients.OthersInGroup(ChatGroups.Conversation(conversationId))
            .UserTyping(conversationId, Context.User!.GetUserId(), DisplayName(), true);
    }

    // Never throttled, and it resets the clock, so typing again straight after stopping is
    // delivered. Stopping in a conversation this connection is not in is a harmless no-op: there
    // is nothing to stop, and failing would only add noise for a clean-up call.
    public async Task StopTyping(Guid conversationId)
    {
        if (!ConnectionState.Joined(Context.Items).ContainsKey(conversationId))
            return;

        ConnectionState.TypingStartedAt(Context.Items).TryRemove(conversationId, out _);

        await Clients.OthersInGroup(ChatGroups.Conversation(conversationId))
            .UserTyping(conversationId, Context.User!.GetUserId(), DisplayName(), false);
    }

    // Taken from the token, never from the client, so nobody can type under another name.
    private string DisplayName() => Context.User!.FindFirst("display_name")?.Value ?? "Someone";

    // Adding the same connection to the same group twice is harmless: a group is a set.
    private async Task JoinGroupAsync(Guid conversationId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, ChatGroups.Conversation(conversationId), Context.ConnectionAborted);

        ConnectionState.Joined(Context.Items)[conversationId] = 0;
    }

    private async Task LeaveGroupAsync(Guid conversationId)
    {
        ConnectionState.Forget(Context.Items, conversationId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, ChatGroups.Conversation(conversationId), Context.ConnectionAborted);
    }

    private async Task LeaveConversationsNoLongerActiveAsync(Guid userId, IReadOnlyCollection<Guid> joined)
    {
        if (joined.Count == 0)
            return;

        var active = (await _conversations.GetActiveConversationIdsForUserAsync(userId, Context.ConnectionAborted)).ToHashSet();

        foreach (var conversationId in joined.Where(id => !active.Contains(id)))
            await LeaveGroupAsync(conversationId);
    }
}
