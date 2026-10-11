using ChatNET.API.Auth.Models;
using ChatNET.API.Common.Exceptions;
using ChatNET.API.Common.Time;
using ChatNET.API.Conversations;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Messaging.Models;
using ChatNET.API.Messaging.Persistence;
using MediatR;
using Microsoft.AspNetCore.Identity;

namespace ChatNET.API.Messaging.SendMessage;

public class SendMessageHandler : IRequestHandler<SendMessageCommand, MessageDto>
{
    private readonly IConversationRepository _conversations;
    private readonly IMessageRepository _messages;
    private readonly UserManager<AppUser> _userManager;
    private readonly ILogger<SendMessageHandler> _logger;

    public SendMessageHandler(
        IConversationRepository conversations,
        IMessageRepository messages,
        UserManager<AppUser> userManager,
        ILogger<SendMessageHandler> logger)
    {
        _conversations = conversations;
        _messages = messages;
        _userManager = userManager;
        _logger = logger;
    }

    public async Task<MessageDto> Handle(SendMessageCommand request, CancellationToken cancellationToken)
    {
        if (!await _conversations.IsActiveMemberAsync(request.ConversationId, request.SenderId, cancellationToken))
        {
            _logger.LogWarning(
                "User {UserId} tried to send to conversation {ConversationId} without being an active member",
                request.SenderId, request.ConversationId);

            throw new ForbiddenException(ConversationErrors.NotAMember);
        }

        // A token stays valid until it expires, even if the account was deactivated after it was
        // issued. Writing content other people read is where that matters, so it is checked here.
        // The user is loaded anyway: the message carries the sender's display name.
        var sender = await _userManager.FindByIdAsync(request.SenderId.ToString());
        if (sender is null || sender.IsDeactivated)
            throw new ForbiddenException("This account can no longer send messages.");

        var message = new Message
        {
            ConversationId = request.ConversationId,
            SenderId = request.SenderId,
            Content = request.Content!.Trim(),
            CreatedAt = DateTime.UtcNow.TruncateToMicroseconds(),
        };

        await _messages.AddAsync(message, cancellationToken);

        _logger.LogInformation(
            "Message {MessageId} sent to conversation {ConversationId} by {UserId}",
            message.Id, message.ConversationId, message.SenderId);

        return new MessageDto(
            message.Id, message.ConversationId, message.SenderId, sender.DisplayName,
            message.Content, message.CreatedAt, request.ClientMessageId);
    }
}
