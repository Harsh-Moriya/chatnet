using ChatNET.API.Notifications.Models;

namespace ChatNET.API.Notifications.Persistence;

public interface INotificationRepository
{
    // The active members of the conversation, other than the sender, whose user name is among those named. A member
    // who left, an account that was deactivated and a name that nobody in the conversation has are all left out,
    // so a mention reveals nothing about who exists elsewhere. The names are in the form Identity stores them.
    Task<List<Guid>> FindMentionedMembersAsync(
        Guid conversationId, Guid senderId, IReadOnlyCollection<string> normalizedUserNames,
        CancellationToken cancellationToken);

    // The position of one of this person's own notifications, or null when there is no such notification of theirs.
    // Someone else's notification is "no such notification", so a cursor reveals nothing about other people's.
    Task<NotificationCursor?> FindCursorAsync(Guid userId, Guid notificationId, CancellationToken cancellationToken);

    // At most "take" of this person's notifications, newest first, read and unread together, starting just after the
    // cursor (or at the newest when there is none).
    Task<List<NotificationDto>> GetPageAsync(
        Guid userId, NotificationCursor? after, int take, CancellationToken cancellationToken);

    // One notification as the bell shows it, whoever it belongs to, or null when there is none. For pushes made from
    // a stored row, which already know whose it is; never for an endpoint that takes the id from a caller.
    Task<NotificationDto?> FindByIdAsync(Guid notificationId, CancellationToken cancellationToken);

    // How many of this person's notifications are unread, counting no further than the cap.
    Task<int> CountUnreadAsync(Guid userId, CancellationToken cancellationToken);

    // Marks this person's own unread notifications read: the given ones, or all of them when ids is null. Someone
    // else's ids are ignored. Returns the ids that were unread, which are the ones that changed.
    Task<List<Guid>> MarkReadAsync(Guid userId, IReadOnlyCollection<Guid>? ids, CancellationToken cancellationToken);

    // Marks this person's unread mentions in the conversation read, as far as the messages they are about were sent
    // at or before upTo: reading a conversation up to a point is reading the mentions up to it. Returns the ids.
    Task<List<Guid>> MarkMentionsReadAsync(
        Guid userId, Guid conversationId, DateTime upTo, CancellationToken cancellationToken);

    // Removes one of this person's own notifications. False when there is no such notification of theirs.
    Task<bool> DeleteAsync(Guid userId, Guid notificationId, CancellationToken cancellationToken);
}
