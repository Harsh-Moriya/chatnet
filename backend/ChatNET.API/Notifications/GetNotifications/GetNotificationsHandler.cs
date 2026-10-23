using ChatNET.API.Notifications.Models;
using ChatNET.API.Notifications.Persistence;
using FluentValidation;
using FluentValidation.Results;
using MediatR;

namespace ChatNET.API.Notifications.GetNotifications;

public class GetNotificationsHandler : IRequestHandler<GetNotificationsQuery, NotificationListDto>
{
    public const int PageSize = 30;
    public const string UnknownCursor = "Unknown notification cursor.";

    private readonly INotificationRepository _notifications;

    public GetNotificationsHandler(INotificationRepository notifications)
    {
        _notifications = notifications;
    }

    public async Task<NotificationListDto> Handle(GetNotificationsQuery request, CancellationToken cancellationToken)
    {
        NotificationCursor? cursor = null;

        if (request.Before is { } before)
        {
            // Looked up among the caller's own notifications, so the id of someone else's gets the same answer as one
            // that does not exist.
            cursor = await _notifications.FindCursorAsync(request.UserId, before, cancellationToken)
                ?? throw new ValidationException([new ValidationFailure(nameof(request.Before), UnknownCursor)]);
        }

        var items = await _notifications.GetPageAsync(request.UserId, cursor, PageSize, cancellationToken);
        var unread = await _notifications.CountUnreadAsync(request.UserId, cancellationToken);

        return new NotificationListDto(items, unread);
    }
}
