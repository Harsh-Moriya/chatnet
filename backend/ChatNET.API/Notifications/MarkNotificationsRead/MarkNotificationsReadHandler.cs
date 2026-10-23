using ChatNET.API.Notifications.Persistence;
using MediatR;

namespace ChatNET.API.Notifications.MarkNotificationsRead;

public class MarkNotificationsReadHandler : IRequestHandler<MarkNotificationsReadCommand, Unit>
{
    private readonly INotificationRepository _notifications;
    private readonly NotificationPusher _pusher;
    private readonly ILogger<MarkNotificationsReadHandler> _logger;

    public MarkNotificationsReadHandler(
        INotificationRepository notifications, NotificationPusher pusher, ILogger<MarkNotificationsReadHandler> logger)
    {
        _notifications = notifications;
        _pusher = pusher;
        _logger = logger;
    }

    public async Task<Unit> Handle(MarkNotificationsReadCommand request, CancellationToken cancellationToken)
    {
        // Only the caller's own notifications can change: the update is scoped to them, so someone else's id in the
        // list is ignored and says nothing about whether it exists.
        var changed = await _notifications.MarkReadAsync(request.UserId, request.Ids, cancellationToken);

        // Nothing was unread (a repeat): nothing to tell the person's other tabs.
        if (changed.Count == 0)
            return Unit.Value;

        _logger.LogInformation("User {UserId} marked {NotificationCount} notifications read", request.UserId, changed.Count);

        await _pusher.ReadAsync(request.UserId, changed);
        return Unit.Value;
    }
}
