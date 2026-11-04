using ChatNET.API.Messaging.Models;
using ChatNET.API.Notifications.Models;
using ChatNET.API.Outbox;
using ChatNET.API.Outbox.Models;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Push;

// Makes the outbox rows that ask the relay to alert people who are away. They are saved in the same transaction as the change they
// are about, like every outbox row, and are for the relay alone: the request does not push them, because who is away and which
// browsers they have is for the relay to find out, a moment later and off the request. A server without push keys makes none, so a
// deployment that does not use push carries no rows for it.
public class PushAlertRows
{
    private readonly OutboxPublisher _outbox;
    private readonly bool _enabled;

    public PushAlertRows(OutboxPublisher outbox, IOptions<PushOptions> options)
    {
        _outbox = outbox;
        _enabled = options.Value.Enabled;
    }

    // For a new message: one row for the message itself and one for each person it names.
    public IReadOnlyList<OutboxMessage> ForMessage(Message message, IReadOnlyCollection<Notification> mentions) =>
        !_enabled
            ? []
            : [
                _outbox.CreateForRelay(new MessageAlertRequested(message.Id, message.ConversationId, [.. mentions.Select(m => m.UserId)])),
                .. ForNotifications(mentions),
            ];

    // For notifications that were stored (mentions, invitations): one row for each.
    public IReadOnlyList<OutboxMessage> ForNotifications(IEnumerable<Notification> notifications) =>
        !_enabled
            ? []
            : notifications.Select(n => _outbox.CreateForRelay(new NotificationAlertRequested(n.Id, n.UserId))).ToList();
}
