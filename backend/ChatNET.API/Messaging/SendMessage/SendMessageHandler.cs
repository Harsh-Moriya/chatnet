using ChatNET.API.Auth.Models;
using ChatNET.API.Common.Exceptions;
using ChatNET.API.Common.Time;
using ChatNET.API.Conversations;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Media;
using ChatNET.API.Messaging.Hubs;
using ChatNET.API.Messaging.Models;
using ChatNET.API.Messaging.Persistence;
using ChatNET.API.Notifications;
using ChatNET.API.Notifications.Models;
using ChatNET.API.Notifications.Persistence;
using ChatNET.API.Outbox;
using ChatNET.API.Outbox.Models;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;

namespace ChatNET.API.Messaging.SendMessage;

public class SendMessageHandler : IRequestHandler<SendMessageCommand, SendMessageResult>
{
    private readonly IConversationRepository _conversations;
    private readonly IMessageRepository _messages;
    private readonly UserManager<AppUser> _userManager;
    private readonly AttachmentIntake _intake;
    private readonly AttachmentLoader _attachmentLoader;
    private readonly INotificationRepository _notifications;
    private readonly OutboxPublisher _outbox;
    private readonly IHubContext<ChatHub, IChatClient> _hub;
    private readonly ILogger<SendMessageHandler> _logger;

    public SendMessageHandler(
        IConversationRepository conversations,
        IMessageRepository messages,
        UserManager<AppUser> userManager,
        AttachmentIntake intake,
        AttachmentLoader attachmentLoader,
        INotificationRepository notifications,
        OutboxPublisher outbox,
        IHubContext<ChatHub, IChatClient> hub,
        ILogger<SendMessageHandler> logger)
    {
        _conversations = conversations;
        _messages = messages;
        _userManager = userManager;
        _intake = intake;
        _attachmentLoader = attachmentLoader;
        _notifications = notifications;
        _outbox = outbox;
        _hub = hub;
        _logger = logger;
    }

    public async Task<SendMessageResult> Handle(SendMessageCommand request, CancellationToken cancellationToken)
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

        // The same send arriving again (the client timed out and tried once more, although the first one had
        // reached the server) carries the client id it used the first time. Checked after the membership and
        // account checks, so someone who has since left, or been deactivated, is refused as for any send.
        if (request.ClientMessageId is { } clientMessageId)
        {
            var earlier = await _messages.FindByClientMessageIdAsync(
                request.ConversationId, request.SenderId, clientMessageId, cancellationToken);

            if (earlier is not null)
                return await RepeatAsync(earlier, cancellationToken);
        }

        var createdAt = DateTime.UtcNow.TruncateToMicroseconds();

        // The files the message names are checked and copied to their permanent place before anything is saved,
        // so a message is never stored with a file that is not there.
        StagedAttachments? staged = null;
        if (request.Attachments is { Count: > 0 } files)
        {
            try
            {
                staged = await _intake.StageAsync(
                    request.ConversationId, request.SenderId, files, createdAt, cancellationToken);
            }
            catch (UploadMissingException) when (request.ClientMessageId is not null)
            {
                // Two copies of one send arriving together: the other attached these files and removed the
                // uploads while this one was looking for them. Its message is the answer.
                var stored = await _messages.FindByClientMessageIdAsync(
                    request.ConversationId, request.SenderId, request.ClientMessageId!.Value, cancellationToken);

                if (stored is not null)
                    return await RepeatAsync(stored, cancellationToken);

                throw;
            }
        }

        // The id is made here, not by the database, because the outbox rows saved with the message name it.
        var message = new Message
        {
            Id = Guid.CreateVersion7(),
            ConversationId = request.ConversationId,
            SenderId = request.SenderId,

            // Empty, not null, for a message of only files: null is how a deleted message is told.
            Content = request.Content?.Trim() ?? string.Empty,
            CreatedAt = createdAt,
            ClientMessageId = request.ClientMessageId,
        };
        if (staged is not null)
            message.Attachments = staged.Attachments;

        var mentions = await MentionsAsync(message, cancellationToken);

        // One row for the message and one for each mention, saved with them. If this request stops before it has
        // pushed, or the push fails, the relay makes the push from these rows.
        var messageRow = _outbox.Create(new MessageCreated(message.Id, message.ConversationId));
        var mentionRows = mentions
            .Select(mention => _outbox.Create(new NotificationCreated(mention.Id, mention.UserId)))
            .ToList();

        // Two copies of one send can arrive together, both find nothing above, and both insert. The unique
        // index lets one in; the other is told so here, and answers with the winner's message. The files this
        // copy made are of no use to anyone then, and are removed, as they are when the save fails.
        bool added;
        try
        {
            added = await _messages.TryAddAsync(message, mentions, [messageRow, .. mentionRows], cancellationToken);
        }
        catch
        {
            if (staged is not null)
                await _intake.DiscardAsync(staged.Attachments);

            throw;
        }

        if (!added)
        {
            if (staged is not null)
                await _intake.DiscardAsync(staged.Attachments);

            var winner = await _messages.FindByClientMessageIdAsync(
                request.ConversationId, request.SenderId, request.ClientMessageId!.Value, cancellationToken);

            return await RepeatAsync(
                winner ?? throw new InvalidOperationException("A send was refused as a repeat but the first one cannot be found."),
                cancellationToken);
        }

        // The message is stored and holds its own copies; what was uploaded for it is no longer needed.
        if (staged is not null)
            await _intake.ReleaseAsync(staged.PendingKeys);

        _logger.LogInformation(
            "Message {MessageId} sent to conversation {ConversationId} by {UserId} with {AttachmentCount} attachments and {MentionCount} mentions",
            message.Id, message.ConversationId, message.SenderId, message.Attachments.Count, mentions.Count);

        var dto = new MessageDto(
            message.Id, message.ConversationId, message.SenderId, sender.DisplayName,
            message.Content, message.CreatedAt, request.ClientMessageId,
            message.Attachments.Select(_attachmentLoader.ToDto).ToList(),
            EditedAt: null, EditCount: 0, IsDeleted: false);

        await _outbox.DeliverAsync(messageRow, () => PushOrThrowAsync(dto), cancellationToken);

        for (var i = 0; i < mentions.Count; i++)
        {
            var mention = mentions[i];
            var notification = new NotificationDto(
                mention.Id, mention.Type, mention.ConversationId, mention.ActorId, sender.DisplayName,
                message.Id, MessagePreview.Of(message.Content), mention.IsRead, mention.CreatedAt);

            await _outbox.DeliverAsync(
                mentionRows[i],
                () => _hub.Clients.User(mention.UserId.ToString()).NotificationReceived(notification),
                cancellationToken);
        }

        return new SendMessageResult(dto, Created: true);
    }

    // The people the message names with "@username", as notifications that are saved in the same transaction as the
    // message. A mention counts in a muted conversation too: someone addressed the person by name. Only active members
    // can be mentioned, so a name says nothing about who exists elsewhere.
    private async Task<List<Notification>> MentionsAsync(Message message, CancellationToken cancellationToken)
    {
        var names = MentionParser.NormalizedUserNamesIn(message.Content);
        if (names.Count == 0)
            return [];

        var recipients = await _notifications.FindMentionedMembersAsync(
            message.ConversationId, message.SenderId, names, cancellationToken);

        return recipients.Select(userId => new Notification
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            ActorId = message.SenderId,
            ConversationId = message.ConversationId,
            Type = NotificationType.Mention,
            CreatedAt = message.CreatedAt,
            Message = message,
        }).ToList();
    }

    // The answer to a send that was already stored: the stored message, not a second one. It is pushed again
    // because the first attempt may have failed at exactly that step (the push is best effort), and a
    // retry is the one moment the server learns of it. A client that already has the message merges the
    // second copy by id, so repeating the push is harmless. The links to its files are new ones.
    private async Task<SendMessageResult> RepeatAsync(MessageDto stored, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "A repeat of message {MessageId} in conversation {ConversationId} by {UserId} returned the stored message",
            stored.Id, stored.ConversationId, stored.SenderId);

        var withFiles = (await _attachmentLoader.AttachToAsync([stored], cancellationToken))[0];

        await PushAsync(withFiles);

        return new SendMessageResult(withFiles, Created: false);
    }

    // The push of a message that was already stored, for a repeated send. The first send's own push is covered by its
    // outbox row, so this one is only a second chance, and a failure of it is not worth more than a warning.
    private async Task PushAsync(MessageDto message)
    {
        try
        {
            await PushOrThrowAsync(message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Could not push message {MessageId} to conversation {ConversationId} again",
                message.Id, message.ConversationId);
        }
    }

    // The group includes the sender's own connections: their sending tab uses the push to confirm its optimistic
    // copy (through ClientMessageId) and their other tabs have no other way to learn about the message.
    private Task PushOrThrowAsync(MessageDto message)
        => _hub.Clients.Group(ChatGroups.Conversation(message.ConversationId)).ReceiveMessage(message);
}
