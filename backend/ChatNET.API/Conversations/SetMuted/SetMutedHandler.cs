using ChatNET.API.Common.Exceptions;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Messaging.Hubs;
using MediatR;
using Microsoft.AspNetCore.SignalR;

namespace ChatNET.API.Conversations.SetMuted;

public class SetMutedHandler : IRequestHandler<SetMutedCommand, Unit>
{
    private readonly IConversationRepository _conversations;
    private readonly IHubContext<ChatHub, IChatClient> _hub;
    private readonly ILogger<SetMutedHandler> _logger;

    public SetMutedHandler(
        IConversationRepository conversations,
        IHubContext<ChatHub, IChatClient> hub,
        ILogger<SetMutedHandler> logger)
    {
        _conversations = conversations;
        _hub = hub;
        _logger = logger;
    }

    public async Task<Unit> Handle(SetMutedCommand request, CancellationToken cancellationToken)
    {
        // The validator has refused a missing value.
        var isMuted = request.IsMuted is true;

        // The repository checks membership and stores the choice in one statement, so there is no gap between
        // the two for a member to leave in.
        var stored = await _conversations.SetMutedAsync(
            request.ConversationId, request.UserId, isMuted, cancellationToken);

        if (!stored)
        {
            _logger.LogWarning(
                "User {UserId} tried to mute conversation {ConversationId} without being an active member",
                request.UserId, request.ConversationId);

            throw new ForbiddenException(ConversationErrors.NotAMember);
        }

        _logger.LogInformation(
            "User {UserId} set muted to {IsMuted} for conversation {ConversationId}",
            request.UserId, isMuted, request.ConversationId);

        await PushAsync(request.ConversationId, request.UserId, isMuted);

        return Unit.Value;
    }

    // Only the person's own tabs hear about it: muting is private to them. The choice is stored, so a failing push
    // (Redis down, for example) must not fail the request; another tab shows it the next time it loads.
    private async Task PushAsync(Guid conversationId, Guid userId, bool isMuted)
    {
        try
        {
            await _hub.Clients.User(userId.ToString()).ConversationMuteChanged(conversationId, isMuted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Could not push the mute choice of user {UserId} for conversation {ConversationId}; their other tabs will see it on their next load",
                userId, conversationId);
        }
    }
}
