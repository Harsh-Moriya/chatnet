using ChatNET.API.Common.Persistence;
using ChatNET.API.Messaging;
using ChatNET.API.Notifications.Models;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Notifications.Persistence;

public class NotificationRepository : INotificationRepository
{
    // The unread count stops here, so the cost of the badge does not grow with how far behind someone is.
    public const int UnreadCap = 100;

    private readonly AppDbContext _db;

    public NotificationRepository(AppDbContext db)
    {
        _db = db;
    }

    public Task<List<Guid>> FindMentionedMembersAsync(
        Guid conversationId, Guid senderId, IReadOnlyCollection<string> normalizedUserNames,
        CancellationToken cancellationToken)
        => _db.ConversationMembers
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId
                        && m.LeftAt == null
                        && m.UserId != senderId
                        && !m.User.IsDeactivated
                        && normalizedUserNames.Contains(m.User.NormalizedUserName!))
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);

    public async Task<NotificationCursor?> FindCursorAsync(
        Guid userId, Guid notificationId, CancellationToken cancellationToken)
    {
        var found = await _db.Notifications
            .AsNoTracking()
            .Where(n => n.Id == notificationId && n.UserId == userId)
            .Select(n => new { n.CreatedAt, n.Id })
            .FirstOrDefaultAsync(cancellationToken);

        return found is null ? null : new NotificationCursor(found.CreatedAt, found.Id);
    }

    // The index is (UserId, IsRead, CreatedAt DESC), so within one value of IsRead the newest rows are at the front
    // and a walk stops after "take" of them. A list that mixes read and unread in time order is two such walks, one
    // for each value, merged: at most 2 x take rows are read, however many notifications someone has. The rows
    // themselves (with the actor's name and the message's text) are fetched afterwards for the winners only.
    public async Task<List<NotificationDto>> GetPageAsync(
        Guid userId, NotificationCursor? after, int take, CancellationToken cancellationToken)
    {
        // The two walks are merged as whole rows and only then cut down to their ids: EF cannot put a set operation
        // after a projection into a type of its own.
        var ids = await Newest(userId, isRead: false, after, take)
            .Concat(Newest(userId, isRead: true, after, take))
            .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
            .Take(take)
            .Select(n => n.Id)
            .ToListAsync(cancellationToken);

        return ids.Count == 0 ? [] : await LoadAsync(ids, cancellationToken);
    }

    public async Task<NotificationDto?> FindByIdAsync(Guid notificationId, CancellationToken cancellationToken)
        => (await LoadAsync([notificationId], cancellationToken)).FirstOrDefault();

    // The notifications with these ids, newest first, with the actor's name and the start of the message's text.
    private async Task<List<NotificationDto>> LoadAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        var rows = await _db.Notifications
            .AsNoTracking()
            .Where(n => ids.Contains(n.Id))
            .Select(n => new
            {
                n.Id,
                n.Type,
                n.ConversationId,
                n.ActorId,
                ActorDisplayName = n.Actor.DisplayName,
                n.MessageId,
                Content = n.Message == null || n.Message.IsDeleted ? null : n.Message.Content,
                n.IsRead,
                n.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        return rows
            .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
            .Select(n => new NotificationDto(
                n.Id, n.Type, n.ConversationId, n.ActorId, n.ActorDisplayName, n.MessageId,
                MessagePreview.Of(n.Content), n.IsRead, n.CreatedAt))
            .ToList();
    }

    // Positions are compared as (CreatedAt, Id), as in the message history: "CreatedAt <=" first, which is what the
    // index walk starts from, then the id for the ones stamped alike.
    private IQueryable<Notification> Newest(Guid userId, bool isRead, NotificationCursor? after, int take)
    {
        var rows = _db.Notifications.AsNoTracking().Where(n => n.UserId == userId && n.IsRead == isRead);

        if (after is { } cursor)
        {
            rows = rows.Where(n => n.CreatedAt <= cursor.CreatedAt
                                   && (n.CreatedAt < cursor.CreatedAt || n.Id.CompareTo(cursor.Id) < 0));
        }

        return rows
            .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
            .Take(take);
    }

    public Task<int> CountUnreadAsync(Guid userId, CancellationToken cancellationToken)
        => _db.Notifications
            .Where(n => n.UserId == userId && !n.IsRead)
            .Take(UnreadCap)
            .CountAsync(cancellationToken);

    public async Task<List<Guid>> MarkReadAsync(
        Guid userId, IReadOnlyCollection<Guid>? ids, CancellationToken cancellationToken)
    {
        var unread = _db.Notifications.Where(n => n.UserId == userId && !n.IsRead);
        if (ids is not null)
            unread = unread.Where(n => ids.Contains(n.Id));

        return await MarkAsync(unread, userId, cancellationToken);
    }

    public Task<List<Guid>> MarkMentionsReadAsync(
        Guid userId, Guid conversationId, DateTime upTo, CancellationToken cancellationToken)
        => MarkAsync(
            _db.Notifications.Where(n => n.UserId == userId
                                         && !n.IsRead
                                         && n.ConversationId == conversationId
                                         && n.Type == NotificationType.Mention
                                         && n.Message != null
                                         && n.Message.CreatedAt <= upTo),
            userId,
            cancellationToken);

    // Finds the rows, then updates exactly those, still only while they are unread and this person's. Two requests
    // racing for the same rows can both report them; marking a row read twice changes nothing and a client applies
    // the report twice without harm.
    private async Task<List<Guid>> MarkAsync(
        IQueryable<Notification> unread, Guid userId, CancellationToken cancellationToken)
    {
        var ids = await unread.Select(n => n.Id).ToListAsync(cancellationToken);
        if (ids.Count == 0)
            return ids;

        await _db.Notifications
            .Where(n => n.UserId == userId && !n.IsRead && ids.Contains(n.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true), cancellationToken);

        return ids;
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid notificationId, CancellationToken cancellationToken)
        => await _db.Notifications
            .Where(n => n.Id == notificationId && n.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken) > 0;
}
