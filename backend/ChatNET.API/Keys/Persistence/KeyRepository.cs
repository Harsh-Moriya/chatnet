using ChatNET.API.Common.Persistence;
using ChatNET.API.Keys.Models;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Keys.Persistence;

public class KeyRepository : IKeyRepository
{
    private readonly AppDbContext _db;

    public KeyRepository(AppDbContext db)
    {
        _db = db;
    }

    public Task<List<AccountKeyBundle>> GetBundlesAsync(Guid userId, CancellationToken cancellationToken) =>
        _db.AccountKeyBundles
            .AsNoTracking()
            .Where(b => b.UserId == userId)
            .OrderBy(b => b.Generation)
            .ToListAsync(cancellationToken);

    public Task<List<AccountKeyBundle>> GetCurrentBundlesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken) =>
        _db.AccountKeyBundles
            .AsNoTracking()
            .Where(b => userIds.Contains(b.UserId))
            .Where(b => b.Generation == _db.AccountKeyBundles.Where(o => o.UserId == b.UserId).Max(o => o.Generation))
            .ToListAsync(cancellationToken);

    public Task<Dictionary<Guid, int>> GetCurrentGenerationsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken) =>
        _db.AccountKeyBundles
            .AsNoTracking()
            .Where(b => userIds.Contains(b.UserId))
            .GroupBy(b => b.UserId)
            .Select(g => new { UserId = g.Key, Generation = g.Max(b => b.Generation) })
            .ToDictionaryAsync(x => x.UserId, x => x.Generation, cancellationToken);

    public Task<bool> HasKeysAsync(Guid userId, CancellationToken cancellationToken) =>
        _db.AccountKeyBundles.AnyAsync(b => b.UserId == userId, cancellationToken);

    public Task<KeyBackup?> GetBackupAsync(Guid userId, CancellationToken cancellationToken) =>
        _db.KeyBackups.AsNoTracking().FirstOrDefaultAsync(b => b.UserId == userId, cancellationToken);

    // The first insert waits for a request that is inserting the same key and then does nothing if that one committed, so the
    // loser learns it lost from the number of rows and not from an exception.
    public async Task<bool> TryCreateAsync(Guid userId, byte[] bundle, byte[] backup, DateTime now, CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var inserted = await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AccountKeyBundles" ("UserId", "Generation", "Bundle", "CreatedAt")
            VALUES ({userId}, 1, {bundle}, {now})
            ON CONFLICT DO NOTHING
            """, cancellationToken);

        if (inserted == 0)
            return false;

        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "KeyBackups" ("UserId", "Blob", "Version", "UpdatedAt")
            VALUES ({userId}, {backup}, 1, {now})
            """, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<int?> TryReplaceBackupAsync(
        Guid userId, byte[] blob, int expectedVersion, DateTime now, CancellationToken cancellationToken)
    {
        var replaced = await _db.KeyBackups
            .Where(b => b.UserId == userId && b.Version == expectedVersion)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Blob, blob)
                .SetProperty(b => b.Version, b => b.Version + 1)
                .SetProperty(b => b.UpdatedAt, now), cancellationToken);

        return replaced == 1 ? expectedVersion + 1 : null;
    }
}
