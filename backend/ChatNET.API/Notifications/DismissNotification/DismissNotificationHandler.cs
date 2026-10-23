using ChatNET.API.Common.Exceptions;
using ChatNET.API.Notifications.Persistence;
using MediatR;

namespace ChatNET.API.Notifications.DismissNotification;

public class DismissNotificationHandler : IRequestHandler<DismissNotificationCommand, Unit>
{
    private readonly INotificationRepository _notifications;
    private readonly NotificationPusher _pusher;
    private readonly ILogger<DismissNotificationHandler> _logger;

    public DismissNotificationHandler(
        INotificationRepository notifications, NotificationPusher pusher, ILogger<DismissNotificationHandler> logger)
    {
        _notifications = notifications;
        _pusher = pusher;
        _logger = logger;
    }

    public async Task<Unit> Handle(DismissNotificationCommand request, CancellationToken cancellationToken)
    {
        // A notification of someone else's gets the same 404 as one that does not exist.
        if (!await _notifications.DeleteAsync(request.UserId, request.NotificationId, cancellationToken))
            throw new NotFoundException("Notification", request.NotificationId);

        _logger.LogInformation("User {UserId} dismissed notification {NotificationId}", request.UserId, request.NotificationId);

        await _pusher.DismissedAsync(request.UserId, request.NotificationId);
        return Unit.Value;
    }
}
