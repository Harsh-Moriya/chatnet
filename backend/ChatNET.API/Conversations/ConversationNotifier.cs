using ChatNET.API.Conversations.Models;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Messaging.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace ChatNET.API.Conversations;

// Tells connected members that a conversation now exists for them. Their open connections are not
// in its SignalR group yet, so this event is how a client learns it should call JoinConversation.
//
// Called only after the conversation is saved, so a client never hears about a conversation that was rolled back,
// and by whoever owns the push (the outbox), which decides what a failure means: this class lets it throw.
public class ConversationNotifier
{
    private readonly IHubContext<ChatHub, IChatClient> _hub;
    private readonly IConversationRepository _conversations;

    public ConversationNotifier(
        IHubContext<ChatHub, IChatClient> hub,
        IConversationRepository conversations)
    {
        _hub = hub;
        _conversations = conversations;
    }

    // creatorView is the conversation as its creator sees it. The creator is told as well: the
    // tab that made the request already has the REST response, but their other open tabs do not.
    public async Task NotifyCreatedAsync(
        ConversationDto creatorView, Guid creatorId, IReadOnlyCollection<Guid> memberIds,
        CancellationToken cancellationToken)
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

    // ##### Changes to a group #####
    // Each of these builds what it sends from the stored rows when it is called, so a push that is made late, or twice,
    // says what is true then and never what was true when the change was made. They go to people one by one (the
    // member's user id) and not to the conversation's group of connections: the group may still hold the connections of
    // someone who was just removed, and they must not be told who is left.

    // The entries of the people in `affected` as they are now, to everyone who is in the group now; the ones among them
    // who are no longer in it are named as gone.
    public async Task NotifyMembersChangedAsync(
        Guid conversationId, IReadOnlyCollection<Guid> affected, CancellationToken cancellationToken)
    {
        var members = await _conversations.GetActiveMembersAsync(conversationId, cancellationToken);
        if (members.Count == 0)
            return;

        var present = members.Select(m => m.UserId).ToHashSet();
        var changed = members.Where(m => affected.Contains(m.UserId)).ToList();
        var gone = affected.Where(id => !present.Contains(id)).Distinct().ToList();

        await _hub.Clients.Users(members.Select(m => m.UserId.ToString()).ToList())
            .MembersChanged(new MembersChangedDto(conversationId, changed, gone));
    }

    // Each person who was added, or came back, is sent the conversation as they see it, which is also how their open
    // connections learn to join its group. Someone who is no longer in it by now is not.
    public async Task NotifyJoinedAsync(
        Guid conversationId, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        foreach (var userId in userIds)
        {
            var view = await _conversations.GetForViewerAsync(conversationId, userId, cancellationToken);
            if (view is { LeftAt: null })
                await _hub.Clients.User(userId.ToString()).AddedToConversation(view);
        }
    }

    // Tells the person's tabs they are out. Nothing is said about someone who is a member again by now.
    public async Task NotifyLeftAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken)
    {
        var leftAt = await _conversations.GetLeftAtAsync(conversationId, userId, cancellationToken);
        if (leftAt is null)
            return;

        await _hub.Clients.User(userId.ToString()).RemovedFromConversation(conversationId, leftAt.Value);
    }

    public async Task NotifyUpdatedAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var update = await _conversations.GetUpdateAsync(conversationId, cancellationToken);
        if (update is null)
            return;

        var memberIds = await _conversations.GetActiveMemberIdsAsync(conversationId, cancellationToken);
        if (memberIds.Count == 0)
            return;

        await _hub.Clients.Users(memberIds.Select(id => id.ToString()).ToList()).ConversationUpdated(update);
    }
}
