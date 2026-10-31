using ChatNET.API.Messaging.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace ChatNET.API.Notifications;

// Tells a person's open tabs that notifications were read or dismissed. Called only after the change is saved, so a
// client never hears about a change that was rolled back. The change is already committed, so a failing push (Redis
// down, for example) must not turn a successful request into an error: a client loads the list again when it
// connects, and shows what it missed then. A new notification is pushed through the outbox instead, because losing
// that one costs the person an alert (see OutboxPublisher).
public class NotificationPusher
{
    private readonly IHubContext<ChatHub, IChatClient> _hub;
    private readonly ILogger<NotificationPusher> _logger;

    public NotificationPusher(IHubContext<ChatHub, IChatClient> hub, ILogger<NotificationPusher> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public async Task ReadAsync(Guid userId, IReadOnlyCollection<Guid> ids)
    {
        try
        {
            await _hub.Clients.User(userId.ToString()).NotificationsRead(ids.ToArray());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Could not tell user {UserId} that {NotificationCount} notifications were read; their other tabs will agree on the next load",
                userId, ids.Count);
        }
    }

    public async Task DismissedAsync(Guid userId, Guid notificationId)
    {
        try
        {
            await _hub.Clients.User(userId.ToString()).NotificationDismissed(notificationId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Could not tell user {UserId} that notification {NotificationId} was dismissed; their other tabs will agree on the next load",
                userId, notificationId);
        }
    }
}
