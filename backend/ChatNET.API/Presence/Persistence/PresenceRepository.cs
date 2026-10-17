using ChatNET.API.Common.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Presence.Persistence;

public class PresenceRepository : IPresenceRepository
{
    private readonly AppDbContext _db;

    public PresenceRepository(AppDbContext db)
    {
        _db = db;
    }

    // One conditional UPDATE, like the read position of a conversation: the database settles which of
    // two racing writers is later, with no read first.
    public Task SetLastSeenAsync(Guid userId, DateTime lastSeenAt, CancellationToken cancellationToken)
        => _db.Users
            .Where(u => u.Id == userId && (u.LastSeenAt == null || u.LastSeenAt < lastSeenAt))
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastSeenAt, lastSeenAt), cancellationToken);

    public async Task<Dictionary<Guid, DateTime?>> GetLastSeenAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
        => await _db.Users
            .AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.LastSeenAt, cancellationToken);
}
