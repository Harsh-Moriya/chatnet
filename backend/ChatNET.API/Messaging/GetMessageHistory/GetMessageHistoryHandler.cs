using ChatNET.API.Common.Exceptions;
using ChatNET.API.Conversations;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Messaging.Models;
using ChatNET.API.Messaging.Persistence;
using MediatR;

namespace ChatNET.API.Messaging.GetMessageHistory;

public class GetMessageHistoryHandler : IRequestHandler<GetMessageHistoryQuery, List<MessageDto>>
{
    public const int PageSize = 50;

    private readonly IConversationRepository _conversations;
    private readonly IMessageRepository _messages;
    private readonly ILogger<GetMessageHistoryHandler> _logger;

    public GetMessageHistoryHandler(
        IConversationRepository conversations,
        IMessageRepository messages,
        ILogger<GetMessageHistoryHandler> logger)
    {
        _conversations = conversations;
        _messages = messages;
        _logger = logger;
    }

    public async Task<List<MessageDto>> Handle(GetMessageHistoryQuery request, CancellationToken cancellationToken)
    {
        // Only active members may read, and only what was sent since they joined: being added to a
        // conversation does not reveal what was said before.
        var joinedAt = await _conversations.GetActiveMemberJoinedAtAsync(
            request.ConversationId, request.UserId, cancellationToken);

        if (joinedAt is null)
        {
            _logger.LogWarning(
                "User {UserId} tried to read the history of conversation {ConversationId} without being an active member",
                request.UserId, request.ConversationId);

            throw new ForbiddenException(ConversationErrors.NotAMember);
        }

        return await _messages.GetLatestAsync(request.ConversationId, joinedAt.Value, PageSize, cancellationToken);
    }
}
