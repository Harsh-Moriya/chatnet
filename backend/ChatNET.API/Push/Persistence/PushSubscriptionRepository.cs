using ChatNET.API.Common.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Push.Persistence;

public class PushSubscriptionRepository : IPushSubscriptionRepository
{
    private readonly AppDbContext _db;

    public PushSubscriptionRepository(AppDbContext db)
    {
        _db = db;
    }

    // One statement for the insert or the update, so two requests that register the same browser together cannot both
    // insert: the unique index on the endpoint lets one in and the other updates its row. The trim that follows is in the
    // same transaction, so a person never has more than the limit for longer than the save takes.
    public async Task SaveAsync(
        Guid userId, string endpoint, string p256dh, string auth, int maxPerUser, DateTime now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "PushSubscriptions" ("Id", "UserId", "Endpoint", "P256dh", "Auth", "CreatedAt", "LastUsedAt")
            VALUES ({Guid.CreateVersion7()}, {userId}, {endpoint}, {p256dh}, {auth}, {now}, {now})
            ON CONFLICT ("Endpoint") DO UPDATE
            SET "UserId" = EXCLUDED."UserId", "P256dh" = EXCLUDED."P256dh", "Auth" = EXCLUDED."Auth",
                "LastUsedAt" = EXCLUDED."LastUsedAt"
            """, cancellationToken);

        // The row just saved has the newest LastUsedAt, so it is never among the ones past the limit.
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM "PushSubscriptions"
            WHERE "Id" IN (
                SELECT "Id" FROM "PushSubscriptions"
                WHERE "UserId" = {userId}
                ORDER BY "LastUsedAt" DESC, "Id" DESC
                OFFSET {maxPerUser})
            """, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> RemoveAsync(Guid userId, string endpoint, CancellationToken cancellationToken) =>
        await _db.PushSubscriptions
            .Where(s => s.UserId == userId && s.Endpoint == endpoint)
            .ExecuteDeleteAsync(cancellationToken) > 0;

    public Task<List<PushTarget>> GetForUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken) =>
        _db.PushSubscriptions
            .AsNoTracking()
            .Where(s => userIds.Contains(s.UserId))
            .Select(s => new PushTarget(s.Id, s.UserId, s.Endpoint, s.P256dh, s.Auth))
            .ToListAsync(cancellationToken);

    public async Task MarkUsedAsync(IReadOnlyCollection<Guid> ids, DateTime now, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
            return;

        await _db.PushSubscriptions
            .Where(s => ids.Contains(s.Id))
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.LastUsedAt, now), cancellationToken);
    }

    public async Task RemoveByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
            return;

        await _db.PushSubscriptions.Where(s => ids.Contains(s.Id)).ExecuteDeleteAsync(cancellationToken);
    }

    public Task<int> RemoveUnusedSinceAsync(DateTime cutoff, CancellationToken cancellationToken) =>
        _db.PushSubscriptions.Where(s => s.LastUsedAt < cutoff).ExecuteDeleteAsync(cancellationToken);

    public Task<List<Guid>> FindMessageRecipientsAsync(
        Guid conversationId, Guid senderId, IReadOnlyCollection<Guid> excluded, CancellationToken cancellationToken) =>
        _db.ConversationMembers
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId
                        && m.LeftAt == null
                        && !m.IsMuted
                        && m.UserId != senderId
                        && !m.User.IsDeactivated
                        && !excluded.Contains(m.UserId)
                        && _db.PushSubscriptions.Any(s => s.UserId == m.UserId))
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);
}
