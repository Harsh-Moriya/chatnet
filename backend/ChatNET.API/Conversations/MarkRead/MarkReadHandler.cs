using ChatNET.API.Common.Exceptions;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Messaging.Hubs;
using ChatNET.API.Messaging.Persistence;
using ChatNET.API.Notifications;
using ChatNET.API.Notifications.Persistence;
using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.AspNetCore.SignalR;

namespace ChatNET.API.Conversations.MarkRead;

public class MarkReadHandler : IRequestHandler<MarkReadCommand, Unit>
{
    public const string UnknownMessage = "Unknown message.";

    private readonly IConversationRepository _conversations;
    private readonly IMessageRepository _messages;
    private readonly INotificationRepository _notifications;
    private readonly NotificationPusher _notificationPusher;
    private readonly IHubContext<ChatHub, IChatClient> _hub;
    private readonly ILogger<MarkReadHandler> _logger;

    public MarkReadHandler(
        IConversationRepository conversations,
        IMessageRepository messages,
        INotificationRepository notifications,
        NotificationPusher notificationPusher,
        IHubContext<ChatHub, IChatClient> hub,
        ILogger<MarkReadHandler> logger)
    {
        _conversations = conversations;
        _messages = messages;
        _notifications = notifications;
        _notificationPusher = notificationPusher;
        _hub = hub;
        _logger = logger;
    }

    public async Task<Unit> Handle(MarkReadCommand request, CancellationToken cancellationToken)
    {
        var joinedAt = await _conversations.GetActiveMemberJoinedAtAsync(
            request.ConversationId, request.UserId, cancellationToken);

        if (joinedAt is null)
        {
            _logger.LogWarning(
                "User {UserId} tried to mark conversation {ConversationId} read without being an active member",
                request.UserId, request.ConversationId);

            throw new ForbiddenException(ConversationErrors.NotAMember);
        }

        // The message has to be one the caller may read: in this conversation, sent since they
        // joined. Anything else is "unknown", so the answer reveals nothing about other messages.
        var message = await _messages.FindCursorAsync(
            request.ConversationId, request.MessageId, joinedAt.Value, cancellationToken);

        if (message is null)
            throw new ValidationException([new ValidationFailure(nameof(request.MessageId), UnknownMessage)]);

        var moved = await _conversations.AdvanceLastReadAsync(
            request.ConversationId, request.UserId, message.Value.CreatedAt, cancellationToken);

        // A read that changes nothing (a repeat, or an older message than the one already read) is
        // not worth telling anyone about.
        if (!moved)
            return Unit.Value;

        _logger.LogInformation(
            "User {UserId} read conversation {ConversationId} up to message {MessageId}",
            request.UserId, request.ConversationId, request.MessageId);

        // Reading a conversation up to a message is reading the mentions of the person in it up to that message too,
        // so their bell does not keep asking about what is already on screen.
        var mentionsRead = await _notifications.MarkMentionsReadAsync(
            request.UserId, request.ConversationId, message.Value.CreatedAt, cancellationToken);

        await PushAsync(request.ConversationId, request.UserId, message.Value.CreatedAt);

        if (mentionsRead.Count > 0)
            await _notificationPusher.ReadAsync(request.UserId, mentionsRead);

        return Unit.Value;
    }

    // After the new position is stored, so nobody is told about a position that was not saved. The
    // position is already committed, so a failing push (Redis down, for example) must not turn a
    // successful request into an error; members see the position the next time they load it.
    private async Task PushAsync(Guid conversationId, Guid userId, DateTime readAt)
    {
        try
        {
            await _hub.Clients.Group(ChatGroups.Conversation(conversationId)).MessageRead(conversationId, userId, readAt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Could not push the read position of user {UserId} in conversation {ConversationId}; members will see it on their next load",
                userId, conversationId);
        }
    }
}
