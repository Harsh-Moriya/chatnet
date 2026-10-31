using ChatNET.API.Auth.Models;
using ChatNET.API.Common.Exceptions;
using ChatNET.API.Common.Time;
using ChatNET.API.Conversations;
using ChatNET.API.Conversations.Models;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Media;
using ChatNET.API.Messaging.Hubs;
using ChatNET.API.Messaging.Models;
using ChatNET.API.Messaging.Persistence;
using ChatNET.API.Outbox;
using ChatNET.API.Outbox.Models;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;

namespace ChatNET.API.Messaging.DeleteMessage;

// The sender may delete their message whenever they like, and the owner or an admin of a group may delete anyone's.
// Deleting keeps the row, so the timeline has no gap, and removes what was said: the text and the files. Deleting a
// message that is already deleted answers with it and does nothing else, so a repeated request is harmless.
public class DeleteMessageHandler : IRequestHandler<DeleteMessageCommand, MessageDto>
{
    public const string NotAllowed = "Only the sender or an admin of the group can delete this message.";

    private readonly IConversationRepository _conversations;
    private readonly IMessageRepository _messages;
    private readonly IAttachmentRepository _attachments;
    private readonly AttachmentRemover _remover;
    private readonly UserManager<AppUser> _userManager;
    private readonly AttachmentLoader _attachmentLoader;
    private readonly OutboxPublisher _outbox;
    private readonly IHubContext<ChatHub, IChatClient> _hub;
    private readonly TimeProvider _time;
    private readonly ILogger<DeleteMessageHandler> _logger;

    public DeleteMessageHandler(
        IConversationRepository conversations,
        IMessageRepository messages,
        IAttachmentRepository attachments,
        AttachmentRemover remover,
        UserManager<AppUser> userManager,
        AttachmentLoader attachmentLoader,
        OutboxPublisher outbox,
        IHubContext<ChatHub, IChatClient> hub,
        TimeProvider time,
        ILogger<DeleteMessageHandler> logger)
    {
        _conversations = conversations;
        _messages = messages;
        _attachments = attachments;
        _remover = remover;
        _userManager = userManager;
        _attachmentLoader = attachmentLoader;
        _outbox = outbox;
        _hub = hub;
        _time = time;
        _logger = logger;
    }

    public async Task<MessageDto> Handle(DeleteMessageCommand request, CancellationToken cancellationToken)
    {
        var joinedAt = await _conversations.GetActiveMemberJoinedAtAsync(
            request.ConversationId, request.UserId, cancellationToken);

        if (joinedAt is null)
        {
            _logger.LogWarning(
                "User {UserId} tried to delete a message of conversation {ConversationId} without being an active member",
                request.UserId, request.ConversationId);

            throw new ForbiddenException(ConversationErrors.NotAMember);
        }

        // A token stays valid until it expires, even if the account was deactivated after it was issued.
        var user = await _userManager.FindByIdAsync(request.UserId.ToString());
        if (user is null || user.IsDeactivated)
            throw new ForbiddenException("This account can no longer send messages.");

        var message = await _messages.FindForChangeAsync(
            request.ConversationId, request.MessageId, joinedAt.Value, cancellationToken)
            ?? throw new NotFoundException("Message", request.MessageId);

        if (message.SenderId != request.UserId && !await IsGroupAdminAsync(request, cancellationToken))
        {
            _logger.LogWarning(
                "User {UserId} tried to delete message {MessageId} of conversation {ConversationId} without being allowed to",
                request.UserId, message.Id, request.ConversationId);

            throw new ForbiddenException(NotAllowed);
        }

        if (message.IsDeleted)
            return await CurrentAsync(message.Id, cancellationToken);

        // Read before the change, though the rows stay either way: what is removed from the bucket is named here.
        var files = await _attachments.GetForMessagesAsync([message.Id], cancellationToken);

        var now = _time.GetUtcNow().UtcDateTime.TruncateToMicroseconds();
        var row = _outbox.Create(new MessageUpdated(message.Id, request.ConversationId));

        // Taking the files out of the bucket is a job of its own, saved with the deletion: if the storage cannot be
        // reached now, or this process stops before it gets there, the relay does it. Only a message with files has one.
        var filesRow = files.Count > 0 ? _outbox.Create(new MessageFilesRemoved(message.Id)) : null;
        List<OutboxMessage> rows = filesRow is null ? [row] : [row, filesRow];

        // False means another request deleted it between the read above and here: it announced the deletion and
        // took the files out.
        if (!await _messages.TryDeleteAsync(message.Id, request.UserId, now, rows, cancellationToken))
            return await CurrentAsync(message.Id, cancellationToken);

        _logger.LogInformation(
            "Message {MessageId} of conversation {ConversationId} deleted by {UserId} with {AttachmentCount} files",
            message.Id, request.ConversationId, request.UserId, files.Count);

        var dto = await CurrentAsync(message.Id, cancellationToken);

        await _outbox.DeliverAsync(
            row,
            () => _hub.Clients.Group(ChatGroups.Conversation(request.ConversationId)).MessageUpdated(dto),
            cancellationToken);

        // Not tied to the request: the request ending now does not make the files any less in the way.
        if (filesRow is not null)
            await _outbox.DeliverAsync(filesRow, () => _remover.RemoveAsync(files, CancellationToken.None), cancellationToken);

        return dto;
    }

    // A direct chat has no admins: both members are plain members, so only the sender may delete there.
    private async Task<bool> IsGroupAdminAsync(DeleteMessageCommand request, CancellationToken cancellationToken)
    {
        var role = await _conversations.GetActiveMemberRoleAsync(request.ConversationId, request.UserId, cancellationToken);
        return role is MemberRole.Owner or MemberRole.Admin;
    }

    private async Task<MessageDto> CurrentAsync(Guid messageId, CancellationToken cancellationToken)
    {
        var message = await _messages.FindByIdAsync(messageId, cancellationToken)
                      ?? throw new NotFoundException("Message", messageId);

        return (await _attachmentLoader.AttachToAsync([message], cancellationToken))[0];
    }
}
