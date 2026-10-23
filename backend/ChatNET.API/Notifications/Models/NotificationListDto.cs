namespace ChatNET.API.Notifications.Models;

// One page of a person's notifications, newest first, and how many of all their notifications are unread. The
// count stops at a cap (NotificationRepository.UnreadCap), so a client shows "99+" and its cost does not grow
// with the number of rows.
public record NotificationListDto(List<NotificationDto> Items, int UnreadCount);

// The position of a notification in the list: where the page that follows it starts.
public readonly record struct NotificationCursor(DateTime CreatedAt, Guid Id);
