using ChatNET.API.Notifications.Models;
using MediatR;

namespace ChatNET.API.Notifications.GetNotifications;

// No cursor means the newest page. Before is the id of the oldest notification the caller already has, and
// returns the page just older than it.
public record GetNotificationsQuery(Guid UserId, Guid? Before = null) : IRequest<NotificationListDto>;
