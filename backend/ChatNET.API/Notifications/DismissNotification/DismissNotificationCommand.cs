using MediatR;

namespace ChatNET.API.Notifications.DismissNotification;

public record DismissNotificationCommand(Guid UserId, Guid NotificationId) : IRequest<Unit>;
