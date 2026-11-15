using ChatNET.API.Common.Exceptions;
using ChatNET.API.Conversations;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Media;
using ChatNET.API.Messaging.GetMessageHistory;
using ChatNET.API.Messaging.Models;
using ChatNET.API.Messaging.Persistence;
using FluentValidation;
using FluentValidation.Results;
using MediatR;

namespace ChatNET.API.Messaging.GetMessageFiles;

public class GetMessageFilesHandler : IRequestHandler<GetMessageFilesQuery, List<MessageDto>>
{
    public const int PageSize = 50;

    private readonly IConversationRepository _conversations;
    private readonly IMessageRepository _messages;
    private readonly AttachmentLoader _attachments;
    private readonly ILogger<GetMessageFilesHandler> _logger;

    public GetMessageFilesHandler(
        IConversationRepository conversations,
        IMessageRepository messages,
        AttachmentLoader attachments,
        ILogger<GetMessageFilesHandler> logger)
    {
        _conversations = conversations;
        _messages = messages;
        _attachments = attachments;
        _logger = logger;
    }

    public async Task<List<MessageDto>> Handle(GetMessageFilesQuery request, CancellationToken cancellationToken)
    {
        // The same window as the history: a member reads what was sent while they were one, and someone who left reads up to when they left.
        var window = await _conversations.GetMemberWindowAsync(
            request.ConversationId, request.UserId, cancellationToken);

        if (window is null)
        {
            _logger.LogWarning(
                "User {UserId} tried to list the files of conversation {ConversationId} without being a member",
                request.UserId, request.ConversationId);

            throw new ForbiddenException(ConversationErrors.NotAMember);
        }

        // The position is looked up inside the caller's window like a history cursor, so the id of any message, one with files or not,
        // is a valid place to continue from, and an id from anywhere else is as unknown as one that does not exist.
        MessageCursor? cursor = null;
        if (request.Before is { } beforeId)
        {
            cursor = await _messages.FindCursorAsync(request.ConversationId, beforeId, window, cancellationToken)
                ?? throw new ValidationException(
                    [new ValidationFailure(nameof(request.Before), GetMessageHistoryHandler.UnknownCursor)]);
        }

        var page = await _messages.GetWithFilesAsync(request.ConversationId, window, cursor, PageSize, cancellationToken);
        return await _attachments.AttachToAsync(page, cancellationToken);
    }
}
