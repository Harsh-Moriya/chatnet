using ChatNET.API.Common.Exceptions;
using ChatNET.API.Conversations;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Media;
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
    private readonly AttachmentLoader _attachments;
    private readonly ILogger<GetMessageHistoryHandler> _logger;

    public GetMessageHistoryHandler(
        IConversationRepository conversations,
        IMessageRepository messages,
        AttachmentLoader attachments,
        ILogger<GetMessageHistoryHandler> logger)
    {
        _conversations = conversations;
        _messages = messages;
        _attachments = attachments;
        _logger = logger;
    }

    public async Task<List<MessageDto>> Handle(GetMessageHistoryQuery request, CancellationToken cancellationToken)
    {
        // Only a member may read, and only what was sent while they were one: being added to a conversation does not
        // reveal what was said before, and leaving it does not reveal what is said after. Someone who left reads the
        // part up to when they left.
        var window = await _conversations.GetMemberWindowAsync(
            request.ConversationId, request.UserId, cancellationToken);

        if (window is null)
        {
            _logger.LogWarning(
                "User {UserId} tried to read the history of conversation {ConversationId} without being a member",
                request.UserId, request.ConversationId);

            throw new ForbiddenException(ConversationErrors.NotAMember);
        }

        if (request.Before is null && request.After is null)
        {
            return await _attachments.AttachToAsync(
                await _messages.GetLatestAsync(request.ConversationId, window, PageSize, cancellationToken), cancellationToken);
        }

        var reading = request.Before.HasValue ? nameof(request.Before) : nameof(request.After);
        var anchorId = request.Before ?? request.After!.Value;

        // The cursor is looked up inside the caller's own window (this conversation, while they were in it), so an id
        // from anywhere else gets the same answer as one that does not exist.
        var cursor = await _messages.FindCursorAsync(request.ConversationId, anchorId, window, cancellationToken);
        if (cursor is null)
            throw new ValidationException([new ValidationFailure(reading, UnknownCursor)]);

        var page = request.Before.HasValue
            ? await _messages.GetBeforeAsync(request.ConversationId, window, cursor.Value, PageSize, cancellationToken)
            : await _messages.GetAfterAsync(request.ConversationId, window, cursor.Value, PageSize, cancellationToken);

        return await _attachments.AttachToAsync(page, cancellationToken);
    }
}
