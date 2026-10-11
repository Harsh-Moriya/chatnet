using ChatNET.API.Conversations.Models;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Messaging.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace ChatNET.API.Conversations;

// Tells connected members that a conversation now exists for them. Their open connections are not
// in its SignalR group yet, so this event is how a client learns it should call JoinConversation.
//
// Called by the handlers only after the conversation is saved, so a client never hears about a
// conversation that was rolled back.
public class ConversationNotifier
{
    private readonly IHubContext<ChatHub, IChatClient> _hub;
    private readonly IConversationRepository _conversations;
    private readonly ILogger<ConversationNotifier> _logger;

    public ConversationNotifier(
        IHubContext<ChatHub, IChatClient> hub,
        IConversationRepository conversations,
        ILogger<ConversationNotifier> logger)
    {
        _hub = hub;
        _conversations = conversations;
        _logger = logger;
    }

    // creatorView is the conversation as its creator sees it. The creator is told as well: the
    // tab that made the request already has the REST response, but their other open tabs do not.
    public async Task NotifyCreatedAsync(
        ConversationDto creatorView, Guid creatorId, IReadOnlyCollection<Guid> memberIds,
        CancellationToken cancellationToken)
    {
        // The data is already saved, so a failing push (Redis down, for example) must not turn a
        // successful request into an error. Clients can still load the conversation with REST, and
        // they re-join their groups on the next connect.
        try
        {
            if (creatorView.Type == ConversationType.Group)
            {
                // A group looks the same to every member, so one payload serves all of them.
                await _hub.Clients.Users(memberIds.Select(id => id.ToString()).ToList())
                    .AddedToConversation(creatorView);
                return;
            }

            // A direct chat is named after the other person, so each side gets its own payload.
            await _hub.Clients.User(creatorId.ToString()).AddedToConversation(creatorView);

            foreach (var otherId in memberIds.Where(id => id != creatorId))
            {
                var otherView = await _conversations.GetForViewerAsync(creatorView.Id, otherId, cancellationToken);
                if (otherView is not null)
                    await _hub.Clients.User(otherId.ToString()).AddedToConversation(otherView);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Could not announce conversation {ConversationId} to its members; clients will find it on their next load",
                creatorView.Id);
        }
    }
}
