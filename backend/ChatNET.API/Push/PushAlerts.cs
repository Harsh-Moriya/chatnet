using System.Collections.Concurrent;
using ChatNET.API.Common.Time;
using ChatNET.API.Conversations.Persistence;
using ChatNET.API.Messaging.Persistence;
using ChatNET.API.Notifications.Models;
using ChatNET.API.Notifications.Persistence;
using ChatNET.API.Presence;
using ChatNET.API.Push.Persistence;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Push;

// Alerts the people who are away. A person is away when they have no live connection: with one, the page itself shows the alert
// (alerts.ts, where the rules for what deserves one are), and a push as well would be a second alert for the same thing. It is
// asked when the relay takes the row and not when the message is sent, so it is the state a moment later that counts.
//
// Delivery is at least once, like everything the relay does. If some browsers could not be reached the whole alert is tried again,
// and the browsers that already have it get it twice; alerts with the same tag replace each other on the screen, so that is one
// alert and not two.
public class PushAlerts
{
    private readonly IPushSubscriptionRepository _subscriptions;
    private readonly IPresenceStore _presence;
    private readonly IPushSender _sender;
    private readonly IMessageRepository _messages;
    private readonly IAttachmentRepository _attachments;
    private readonly INotificationRepository _notifications;
    private readonly IConversationRepository _conversations;
    private readonly PushOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PushAlerts> _logger;

    public PushAlerts(
        IPushSubscriptionRepository subscriptions,
        IPresenceStore presence,
        IPushSender sender,
        IMessageRepository messages,
        IAttachmentRepository attachments,
        INotificationRepository notifications,
        IConversationRepository conversations,
        IOptions<PushOptions> options,
        TimeProvider time,
        ILogger<PushAlerts> logger)
    {
        _subscriptions = subscriptions;
        _presence = presence;
        _sender = sender;
        _messages = messages;
        _attachments = attachments;
        _notifications = notifications;
        _conversations = conversations;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    // A new message: every active member except the sender who has not muted the conversation, is not named by the message (they are
    // alerted by the mention) and is away.
    public async Task AlertForMessageAsync(
        Guid messageId, Guid conversationId, IReadOnlyCollection<Guid> mentionedUserIds, CancellationToken cancellationToken)
    {
        // A server whose keys were removed has rows from before: nothing to do, and they are finished.
        if (!_options.Enabled)
            return;

        var message = await _messages.FindByIdAsync(messageId, cancellationToken);

        // Deleted before the alert: nothing left to say.
        if (message is null || message.IsDeleted)
            return;

        var recipients = await _subscriptions.FindMessageRecipientsAsync(
            conversationId, message.SenderId, mentionedUserIds, cancellationToken);

        var away = await AwayAsync(recipients, cancellationToken);
        if (away.Count == 0)
            return;

        var group = await _conversations.GetUpdateAsync(conversationId, cancellationToken);
        var files = (await _attachments.GetForMessagesAsync([messageId], cancellationToken)).Count;

        await SendAsync(away, PushAlertText.ForMessage(message, group?.Name, files), cancellationToken);
    }

    // A notification: its recipient, if they are away. A mention alerts in a muted conversation too, because someone addressed the
    // person by name.
    public async Task AlertForNotificationAsync(Guid notificationId, Guid userId, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
            return;

        var notification = await _notifications.FindByIdAsync(notificationId, cancellationToken);

        // Dismissed or read before the alert (somewhere else), or about nothing that can be opened: nothing to alert about.
        if (notification is not { IsRead: false, ConversationId: { } conversationId })
            return;

        var away = await AwayAsync([userId], cancellationToken);
        if (away.Count == 0)
            return;

        var group = await _conversations.GetUpdateAsync(conversationId, cancellationToken);
        var alert = notification.Type switch
        {
            NotificationType.Mention => PushAlertText.ForMention(notification, conversationId, group?.Name),
            NotificationType.MemberAdded => PushAlertText.ForInvitation(notification, conversationId, group?.Name),
            _ => null,
        };

        if (alert is not null)
            await SendAsync(away, alert, cancellationToken);
    }

    private async Task<List<Guid>> AwayAsync(IReadOnlyCollection<Guid> people, CancellationToken cancellationToken)
    {
        if (people.Count == 0)
            return [];

        var connected = await _presence.GetConnectedAsync(people, cancellationToken);
        return people.Where(person => !connected.Contains(person)).ToList();
    }

    private async Task SendAsync(IReadOnlyCollection<Guid> people, PushAlert alert, CancellationToken cancellationToken)
    {
        var targets = await _subscriptions.GetForUsersAsync(people, cancellationToken);
        if (targets.Count == 0)
            return;

        var sent = new ConcurrentBag<Guid>();
        var gone = new ConcurrentBag<Guid>();
        var unavailable = 0;

        await Parallel.ForEachAsync(
            targets,
            new ParallelOptions { MaxDegreeOfParallelism = _options.MaxParallelSends, CancellationToken = cancellationToken },
            async (target, token) =>
            {
                switch (await _sender.SendAsync(target, alert, token))
                {
                    case PushOutcome.Sent:
                        sent.Add(target.Id);
                        break;
                    case PushOutcome.Gone:
                        gone.Add(target.Id);
                        break;
                    case PushOutcome.Unavailable:
                        Interlocked.Increment(ref unavailable);
                        break;
                }
            });

        // Before anything is thrown, so what happened is kept even when the alert has to be tried again.
        await _subscriptions.MarkUsedAsync(sent, _time.GetUtcNow().UtcDateTime.TruncateToMicroseconds(), cancellationToken);
        await _subscriptions.RemoveByIdsAsync(gone, cancellationToken);

        _logger.LogInformation(
            "Alert for conversation {ConversationId} sent to {SentCount} of {BrowserCount} browsers; {GoneCount} removed, {UnavailableCount} could not be reached",
            alert.ConversationId, sent.Count, targets.Count, gone.Count, unavailable);

        if (unavailable > 0)
            throw new PushDeliveryException(unavailable);
    }
}
