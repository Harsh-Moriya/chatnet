using ChatNET.API.Auth.Models;
using ChatNET.API.Common.Exceptions;
using ChatNET.API.Common.Time;
using ChatNET.API.Conversations;
using ChatNET.API.Conversations.KeyEpochs;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Keys;
using ChatNET.API.Media;
using ChatNET.API.Messaging.Hubs;
using ChatNET.API.Messaging.Models;
using ChatNET.API.Messaging.Persistence;
using ChatNET.API.Notifications.Models;
using ChatNET.API.Notifications.Persistence;
using ChatNET.API.Outbox;
using ChatNET.API.Outbox.Models;
using ChatNET.API.Push;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Messaging.EditMessage;

// Only the sender edits, and only for a while after sending (Messaging:EditWindow). The new text arrives sealed, like the first one,
// under the epoch the conversation is in now, and signed for the next edit count; the server cannot read it, so it settles who may
// edit and whether the edit is still the next one, and one conditional UPDATE in the repository decides the races (an edit and a delete,
// two edits, a removal that marks the keys as due).
public class EditMessageHandler : IRequestHandler<EditMessageCommand, MessageDto>
{
    public const string NotTheSender = "Only the sender can edit a message.";
    public const string Deleted = "This message was deleted.";
    public const string TooLate = "This message can no longer be edited.";
    public const string EditCountStale = "This message was edited somewhere else first. Look at its latest version.";

    private readonly IConversationRepository _conversations;
    private readonly IMessageRepository _messages;
    private readonly INotificationRepository _notifications;
    private readonly UserManager<AppUser> _userManager;
    private readonly AttachmentLoader _attachmentLoader;
    private readonly OutboxPublisher _outbox;
    private readonly PushAlertRows _alerts;
    private readonly IHubContext<ChatHub, IChatClient> _hub;
    private readonly TimeProvider _time;
    private readonly MessagingOptions _options;
    private readonly ILogger<EditMessageHandler> _logger;

    public EditMessageHandler(
        IConversationRepository conversations,
        IMessageRepository messages,
        INotificationRepository notifications,
        UserManager<AppUser> userManager,
        AttachmentLoader attachmentLoader,
        OutboxPublisher outbox,
        PushAlertRows alerts,
        IHubContext<ChatHub, IChatClient> hub,
        TimeProvider time,
        IOptions<MessagingOptions> options,
        ILogger<EditMessageHandler> logger)
    {
        _conversations = conversations;
        _messages = messages;
        _notifications = notifications;
        _userManager = userManager;
        _attachmentLoader = attachmentLoader;
        _outbox = outbox;
        _alerts = alerts;
        _hub = hub;
        _time = time;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<MessageDto> Handle(EditMessageCommand request, CancellationToken cancellationToken)
    {
        var joinedAt = await _conversations.GetActiveMemberJoinedAtAsync(
            request.ConversationId, request.UserId, cancellationToken);

        if (joinedAt is null)
        {
            _logger.LogWarning(
                "User {UserId} tried to edit a message of conversation {ConversationId} without being an active member",
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

        if (message.SenderId != request.UserId)
        {
            _logger.LogWarning(
                "User {UserId} tried to edit message {MessageId} of conversation {ConversationId}, which is not theirs",
                request.UserId, message.Id, request.ConversationId);

            throw new ForbiddenException(NotTheSender);
        }

        if (message.IsDeleted)
            throw new ConflictException(Deleted);

        var now = _time.GetUtcNow().UtcDateTime.TruncateToMicroseconds();
        var notBefore = now - _options.EditWindow;
        if (message.CreatedAt <= notBefore)
            throw new ConflictException(TooLate);

        // The new text was signed for the next edit count. One made from an older version is refused here, before anything is
        // looked up, and again by the UPDATE, which is what settles two edits that arrive together.
        var editCount = request.EditCount!.Value;
        if (editCount != message.EditCount + 1)
            throw new ConflictException(EditCountStale, MessageCodes.EditCountStale);

        // The validator has accepted the blob, so it decodes.
        var blob = MessageBlob.Decode(request.Ciphertext)!;
        var keyEpoch = MessageBlob.EpochOf(blob);

        var (named, newlyNamed) = await MentionsAsync(message, request, cancellationToken);
        var mentions = newlyNamed.Select(userId => new Notification
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            ActorId = request.UserId,
            ConversationId = request.ConversationId,
            MessageId = message.Id,
            Type = NotificationType.Mention,
            CreatedAt = now,
        }).ToList();

        var updateRow = _outbox.Create(new MessageUpdated(message.Id, request.ConversationId));
        var mentionRows = mentions
            .Select(mention => _outbox.Create(new NotificationCreated(mention.Id, mention.UserId)))
            .ToList();
        var alertRows = _alerts.ForNotifications(mentions);

        var outcome = await _messages.TryEditAsync(
            request.ConversationId, message.Id, blob, keyEpoch, editCount, now, notBefore, named, mentions,
            [updateRow, .. mentionRows, .. alertRows], cancellationToken);

        switch (outcome)
        {
            case EditOutcome.Edited:
                break;
            case EditOutcome.EpochStale:
                throw new ConflictException("The keys of this conversation changed. Look at the conversation again.", KeyEpochCodes.Stale);
            case EditOutcome.EpochDue:
                throw new ConflictException("The keys of this conversation must be replaced before anything more is said.", KeyEpochCodes.Due);
            case EditOutcome.Deleted:
                throw new ConflictException(Deleted);
            case EditOutcome.EditCountStale:
                throw new ConflictException(EditCountStale, MessageCodes.EditCountStale);
            default:
                throw new ConflictException(TooLate);
        }

        _logger.LogInformation(
            "Message {MessageId} of conversation {ConversationId} edited by {UserId} with {MentionCount} new mentions",
            message.Id, request.ConversationId, request.UserId, mentions.Count);

        var dto = await CurrentAsync(message.Id, cancellationToken);

        await _outbox.DeliverAsync(
            updateRow,
            () => _hub.Clients.Group(ChatGroups.Conversation(request.ConversationId)).MessageUpdated(dto),
            cancellationToken);

        for (var i = 0; i < mentions.Count; i++)
        {
            var mention = mentions[i];
            var notification = new NotificationDto(
                mention.Id, mention.Type, mention.ConversationId, mention.ActorId, user.DisplayName,
                message.Id, NotificationMessageDto.Of(dto), mention.IsRead, mention.CreatedAt);

            await _outbox.DeliverAsync(
                mentionRows[i],
                () => _hub.Clients.User(mention.UserId.ToString()).NotificationReceived(notification),
                cancellationToken);
        }

        return dto;
    }

    // The people the new text names, as the sender says (the server cannot read it): the active members among them other than the
    // sender, and, of those, the ones the text before it did not name. They are notified now; the ones already named were notified
    // when they were first named. A name taken out is not un-notified. Only active members can be mentioned.
    private async Task<(IReadOnlyList<Guid> Named, IReadOnlyList<Guid> NewlyNamed)> MentionsAsync(
        MessageForChange message, EditMessageCommand request, CancellationToken cancellationToken)
    {
        if (request.MentionedUserIds is not { Count: > 0 } requested)
            return ([], []);

        var named = await _notifications.FindMentionableMemberIdsAsync(
            request.ConversationId, request.UserId, requested, cancellationToken);

        return (named, named.Where(userId => !message.MentionedUserIds.Contains(userId)).ToList());
    }

    private async Task<MessageDto> CurrentAsync(Guid messageId, CancellationToken cancellationToken)
    {
        var message = await _messages.FindByIdAsync(messageId, cancellationToken)
                      ?? throw new NotFoundException("Message", messageId);

        return (await _attachmentLoader.AttachToAsync([message], cancellationToken))[0];
    }
}
