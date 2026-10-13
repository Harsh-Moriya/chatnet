using ChatNET.API.Common.Exceptions;
using ChatNET.API.Conversations;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Messaging.Models;
using ChatNET.API.Messaging.Persistence;
using FluentValidation;
using FluentValidation.Results;
using MediatR;

namespace ChatNET.API.Messaging.GetMessageHistory;

public class GetMessageHistoryHandler : IRequestHandler<GetMessageHistoryQuery, List<MessageDto>>
{
    public const int PageSize = 50;
    public const string UnknownCursor = "Unknown message cursor.";

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

        var since = joinedAt.Value;

        if (request.Before is null && request.After is null)
            return await _messages.GetLatestAsync(request.ConversationId, since, PageSize, cancellationToken);

        var reading = request.Before.HasValue ? nameof(request.Before) : nameof(request.After);
        var anchorId = request.Before ?? request.After!.Value;

        // The cursor is looked up inside the caller's own window (this conversation, since they
        // joined), so an id from anywhere else gets the same answer as one that does not exist.
        var cursor = await _messages.FindCursorAsync(request.ConversationId, anchorId, since, cancellationToken);
        if (cursor is null)
            throw new ValidationException([new ValidationFailure(reading, UnknownCursor)]);

        return request.Before.HasValue
            ? await _messages.GetBeforeAsync(request.ConversationId, since, cursor.Value, PageSize, cancellationToken)
            : await _messages.GetAfterAsync(request.ConversationId, since, cursor.Value, PageSize, cancellationToken);
    }
}
