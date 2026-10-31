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
using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Messaging.EditMessage;

// Only the sender edits, and only for a while after sending (Messaging:EditWindow). An edit of a message that was
// deleted in the meantime changes nothing: one conditional UPDATE in the repository settles that race.
public class EditMessageHandler : IRequestHandler<EditMessageCommand, MessageDto>
{
    public const string NotTheSender = "Only the sender can edit a message.";
    public const string Deleted = "This message was deleted.";
    public const string NothingToSay = "A message needs something to say or a file to show.";
    public const string TooLate = "This message can no longer be edited.";

    private readonly IConversationRepository _conversations;
    private readonly IMessageRepository _messages;
    private readonly INotificationRepository _notifications;
    private readonly UserManager<AppUser> _userManager;
    private readonly AttachmentLoader _attachmentLoader;
    private readonly OutboxPublisher _outbox;
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

        var content = request.Content!.Trim();
        if (content.Length == 0 && message.AttachmentCount == 0)
            throw new ValidationException([new ValidationFailure(nameof(request.Content), NothingToSay)]);

        // Saving what is already there is not an edit: nothing is counted, saved or pushed.
        if (content == message.Content)
            return await CurrentAsync(message.Id, cancellationToken);

        var mentions = await NewMentionsAsync(message, content, now, request, cancellationToken);
        var updateRow = _outbox.Create(new MessageUpdated(message.Id, request.ConversationId));
        var mentionRows = mentions
            .Select(mention => _outbox.Create(new NotificationCreated(mention.Id, mention.UserId)))
            .ToList();

        var edited = await _messages.TryEditAsync(
            message.Id, content, now, notBefore, mentions, [updateRow, .. mentionRows], cancellationToken);

        if (!edited)
            throw new ConflictException(TooLate);

        _logger.LogInformation(
            "Message {MessageId} of conversation {ConversationId} edited by {UserId} with {MentionCount} new mentions",
            message.Id, request.ConversationId, request.UserId, mentions.Count);

        var dto = await CurrentAsync(message.Id, cancellationToken);

        await _outbox.DeliverAsync(
            updateRow,
            () => _hub.Clients.Group(ChatGroups.Conversation(request.ConversationId)).MessageUpdated(dto));

        for (var i = 0; i < mentions.Count; i++)
        {
            var mention = mentions[i];
            var notification = new NotificationDto(
                mention.Id, mention.Type, mention.ConversationId, mention.ActorId, user.DisplayName,
                message.Id, MessagePreview.Of(content), mention.IsRead, mention.CreatedAt);

            await _outbox.DeliverAsync(
                mentionRows[i],
                () => _hub.Clients.User(mention.UserId.ToString()).NotificationReceived(notification));
        }

        return dto;
    }

    // The people the edit names who the text before it did not: they are notified now, the ones already named were
    // notified when the message was sent. A name taken out is not un-notified. Only active members can be mentioned.
    private async Task<List<Notification>> NewMentionsAsync(
        MessageForChange message, string content, DateTime now, EditMessageCommand request,
        CancellationToken cancellationToken)
    {
        var before = MentionParser.NormalizedUserNamesIn(message.Content).ToHashSet();
        var added = MentionParser.NormalizedUserNamesIn(content).Where(name => !before.Contains(name)).ToList();
        if (added.Count == 0)
            return [];

        var recipients = await _notifications.FindMentionedMembersAsync(
            request.ConversationId, request.UserId, added, cancellationToken);

        return recipients.Select(userId => new Notification
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            ActorId = request.UserId,
            ConversationId = request.ConversationId,
            MessageId = message.Id,
            Type = NotificationType.Mention,
            CreatedAt = now,
        }).ToList();
    }

    private async Task<MessageDto> CurrentAsync(Guid messageId, CancellationToken cancellationToken)
    {
        var message = await _messages.FindByIdAsync(messageId, cancellationToken)
                      ?? throw new NotFoundException("Message", messageId);

        return (await _attachmentLoader.AttachToAsync([message], cancellationToken))[0];
    }
}
