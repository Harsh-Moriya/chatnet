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
    private const string TypingKey = "typing-started-at";

    // The shortest time between two delivered "typing" events from one connection in one
    // conversation. A client should send one every couple of seconds while someone types.
    private static readonly TimeSpan TypingInterval = TimeSpan.FromSeconds(1);

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
    //
    // The event is sent to everyone in the conversation's group, so the sender must really be in it:
    // otherwise anyone who knew a conversation ID could push "X is typing" into a private
    // conversation. The check uses the set of conversations this connection joined (no query).
    // Typing is also limited to one event per interval per conversation, so a misbehaving client
    // cannot turn each keystroke into a message to every member.
    public async Task StartTyping(Guid conversationId)
    {
        if (!JoinedConversations().ContainsKey(conversationId))
            throw new HubException(NotAMember);

        var now = Environment.TickCount64;
        var lastSent = TypingStartedAt();

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
        if (!JoinedConversations().ContainsKey(conversationId))
            return;

        TypingStartedAt().TryRemove(conversationId, out _);

        await Clients.OthersInGroup(ChatGroups.Conversation(conversationId))
            .UserTyping(conversationId, Context.User!.GetUserId(), DisplayName(), false);
    }

    // Taken from the token, never from the client, so nobody can type under another name.
    private string DisplayName() => Context.User!.FindFirst("display_name")?.Value ?? "Someone";

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

    // When this connection last had a "typing" event delivered, per conversation (a monotonic
    // millisecond clock, so a change of system time cannot affect it). Bounded by the number of
    // conversations the connection joined.
    private ConcurrentDictionary<Guid, long> TypingStartedAt()
    {
        if (Context.Items.TryGetValue(TypingKey, out var existing))
            return (ConcurrentDictionary<Guid, long>)existing!;

        var created = new ConcurrentDictionary<Guid, long>();
        Context.Items[TypingKey] = created;
        return created;
    }
}
