using ChatNET.API.Conversations;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Media;
using ChatNET.API.Messaging.Hubs;
using ChatNET.API.Messaging.Models;
using ChatNET.API.Messaging.Persistence;
using ChatNET.API.Notifications.Persistence;
using ChatNET.API.Outbox.Models;
using Microsoft.AspNetCore.SignalR;

namespace ChatNET.API.Outbox;

// Does the job a stored row stands for (a push, or taking files out of the bucket), the way the relay needs it: from the
// stored data as it is now, and with failures thrown so the relay can record them. (The request that wrote the row
// does the job from what it already holds.)
//
// A row whose subject is gone (a notification that was dismissed before the push, for example) has nothing left to
// say, and counts as done.
public class OutboxDispatcher
{
    private readonly IHubContext<ChatHub, IChatClient> _hub;
    private readonly IMessageRepository _messages;
    private readonly AttachmentLoader _attachmentLoader;
    private readonly INotificationRepository _notifications;
    private readonly IConversationRepository _conversations;
    private readonly ConversationNotifier _notifier;
    private readonly IAttachmentRepository _attachments;
    private readonly AttachmentRemover _remover;
    private readonly ILogger<OutboxDispatcher> _logger;

    public OutboxDispatcher(
        IHubContext<ChatHub, IChatClient> hub,
        IMessageRepository messages,
        AttachmentLoader attachmentLoader,
        INotificationRepository notifications,
        IConversationRepository conversations,
        ConversationNotifier notifier,
        IAttachmentRepository attachments,
        AttachmentRemover remover,
        ILogger<OutboxDispatcher> logger)
    {
        _hub = hub;
        _messages = messages;
        _attachmentLoader = attachmentLoader;
        _notifications = notifications;
        _conversations = conversations;
        _notifier = notifier;
        _attachments = attachments;
        _remover = remover;
        _logger = logger;
    }

    public async Task PushAsync(OutboxMessage row, CancellationToken cancellationToken)
    {
        switch (OutboxEvents.Read(row.Kind, row.Payload))
        {
            case MessageCreated created:
                var message = await LoadMessageAsync(created.MessageId, cancellationToken);
                if (message is null)
                {
                    LogNothingToPush(row);
                    return;
                }

                await _hub.Clients.Group(ChatGroups.Conversation(created.ConversationId)).ReceiveMessage(message);
                return;

            case MessageUpdated updated:
                var changed = await LoadMessageAsync(updated.MessageId, cancellationToken);
                if (changed is null)
                {
                    LogNothingToPush(row);
                    return;
                }

                await _hub.Clients.Group(ChatGroups.Conversation(updated.ConversationId)).MessageUpdated(changed);
                return;

            case MessageFilesRemoved removed:
                await RemoveFilesAsync(removed, row, cancellationToken);
                return;

            case NotificationCreated notification:
                var dto = await _notifications.FindByIdAsync(notification.NotificationId, cancellationToken);
                if (dto is null)
                {
                    LogNothingToPush(row);
                    return;
                }

                await _hub.Clients.User(notification.UserId.ToString()).NotificationReceived(dto);
                return;

            case ConversationCreated conversation:
                var view = await _conversations.GetForViewerAsync(
                    conversation.ConversationId, conversation.CreatorId, cancellationToken);
                if (view is null)
                {
                    LogNothingToPush(row);
                    return;
                }

                await _notifier.NotifyCreatedAsync(
                    view, conversation.CreatorId, conversation.MemberIds.ToList(), cancellationToken);
                return;

            case ConversationRead read:
                await _hub.Clients.Group(ChatGroups.Conversation(read.ConversationId))
                    .MessageRead(read.ConversationId, read.UserId, read.ReadAt);
                return;

            default:
                throw new InvalidOperationException($"The outbox has no push for kind '{row.Kind}'.");
        }
    }

    // Removes the files of a deleted message. A row that names a message that is not deleted removes nothing, whatever
    // put it there: the files of a message people can still read must never go. A file already gone counts as removed.
    private async Task RemoveFilesAsync(MessageFilesRemoved removed, OutboxMessage row, CancellationToken cancellationToken)
    {
        var message = await _messages.FindByIdAsync(removed.MessageId, cancellationToken);
        if (message is null)
        {
            LogNothingToPush(row);
            return;
        }

        if (!message.IsDeleted)
        {
            _logger.LogWarning(
                "Outbox {OutboxId} asks for the files of message {MessageId} to be removed, but it is not deleted; nothing removed",
                row.Id, removed.MessageId);

            return;
        }

        var files = await _attachments.GetForMessagesAsync([removed.MessageId], cancellationToken);
        await _remover.RemoveAsync(files, cancellationToken);
    }

    // The message as it is now, with links to its files signed now. A deleted message has no text and no files.
    private async Task<MessageDto?> LoadMessageAsync(Guid messageId, CancellationToken cancellationToken)
    {
        var message = await _messages.FindByIdAsync(messageId, cancellationToken);
        return message is null ? null : (await _attachmentLoader.AttachToAsync([message], cancellationToken))[0];
    }

    private void LogNothingToPush(OutboxMessage row)
        => _logger.LogInformation(
            "Outbox {OutboxId} ({Kind}) names something that no longer exists; nothing to push",
            row.Id, row.Kind);
}
