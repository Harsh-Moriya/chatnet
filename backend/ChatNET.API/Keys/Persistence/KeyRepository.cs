using ChatNET.API.Common.Persistence;
using ChatNET.API.Devices.Persistence;
using ChatNET.API.Keys.Models;
using ChatNET.API.Outbox.Models;
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

    public Task<int?> TryReplaceBackupAsync(
        Guid userId, byte[] blob, int expectedVersion, DateTime now, CancellationToken cancellationToken) =>
        ReplaceBackupAsync(userId, blob, expectedVersion, now, cancellationToken);

    private async Task<int?> ReplaceBackupAsync(
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

    public async Task<Dictionary<Guid, int>> HoldCurrentGenerationsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        await KeyGenerationLock.HoldSharedAsync(_db, userIds, cancellationToken);
        return await GetCurrentGenerationsAsync(userIds, cancellationToken);
    }

    public async Task<GenerationResult> TryAddGenerationAsync(
        Guid userId, int generation, byte[] bundle, byte[] backup, int expectedBackupVersion, Guid? removeDeviceId,
        Func<Guid, OutboxMessage> outboxFor, DateTime now, CancellationToken cancellationToken)
    {
        // Every refusal below returns before the commit, which rolls back what was written so far: a refused backup leaves no bundle
        // and no removed device.
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        await KeyGenerationLock.HoldExclusiveAsync(_db, userId, cancellationToken);

        var newest = await _db.AccountKeyBundles
            .Where(b => b.UserId == userId)
            .MaxAsync(b => (int?)b.Generation, cancellationToken);

        if (newest is null)
            return GenerationResult.Refused(GenerationOutcome.NoKeys);

        if (generation != newest + 1)
            return GenerationResult.Refused(GenerationOutcome.GenerationStale);

        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AccountKeyBundles" ("UserId", "Generation", "Bundle", "CreatedAt")
            VALUES ({userId}, {generation}, {bundle}, {now})
            """, cancellationToken);

        var version = await ReplaceBackupAsync(userId, backup, expectedBackupVersion, now, cancellationToken);
        if (version is null)
            return GenerationResult.Refused(GenerationOutcome.BackupVersionStale);

        if (removeDeviceId is { } deviceId && !await DeviceRemoval.TryRemoveAsync(_db, userId, deviceId, now, cancellationToken))
            return GenerationResult.Refused(GenerationOutcome.DeviceNotFound);

        var announced = await MarkConversationsAsync(userId, outboxFor, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new GenerationResult(GenerationOutcome.Added, version.Value, announced);
    }

    public async Task<GenerationResult> TryResetAsync(
        Guid userId, int generation, byte[] bundle, byte[] backup, Func<Guid, OutboxMessage> outboxFor, DateTime now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        await KeyGenerationLock.HoldExclusiveAsync(_db, userId, cancellationToken);

        var newest = await _db.AccountKeyBundles
            .Where(b => b.UserId == userId)
            .MaxAsync(b => (int?)b.Generation, cancellationToken);

        if (newest is null)
            return GenerationResult.Refused(GenerationOutcome.NoKeys);

        if (generation != newest + 1)
            return GenerationResult.Refused(GenerationOutcome.GenerationStale);

        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "AccountKeyBundles" ("UserId", "Generation", "Bundle", "CreatedAt")
            VALUES ({userId}, {generation}, {bundle}, {now})
            """, cancellationToken);

        // Not conditional on a version: the backup it replaces is one nobody can open any more, which is why the keys are being reset.
        var versions = await _db.Database.SqlQuery<int>($"""
            INSERT INTO "KeyBackups" ("UserId", "Blob", "Version", "UpdatedAt")
            VALUES ({userId}, {backup}, 1, {now})
            ON CONFLICT ("UserId") DO UPDATE
            SET "Blob" = EXCLUDED."Blob", "Version" = "KeyBackups"."Version" + 1, "UpdatedAt" = EXCLUDED."UpdatedAt"
            RETURNING "Version" AS "Value"
            """).ToListAsync(cancellationToken);

        // Every device held the old keys, so none of them is the person's any more, and nobody can tell which of them was lost.
        await DeviceRemoval.RemoveAllAsync(_db, userId, now, cancellationToken);

        var announced = await MarkConversationsAsync(userId, outboxFor, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new GenerationResult(GenerationOutcome.Added, versions.Single(), announced);
    }

    // Conversations the person is in now. The ones they left are not theirs to read any more, and a person who joins later is sealed
    // the newest generation. Saved with the row that announces each, in the transaction the caller has open.
    private async Task<List<(Guid ConversationId, OutboxMessage Row)>> MarkConversationsAsync(
        Guid userId, Func<Guid, OutboxMessage> outboxFor, CancellationToken cancellationToken)
    {
        var conversationIds = await _db.Database.SqlQuery<Guid>($"""
            UPDATE "Conversations" AS c
            SET "KeyRotationRequired" = TRUE
            FROM "ConversationMembers" AS m
            WHERE m."ConversationId" = c."Id" AND m."UserId" = {userId} AND m."LeftAt" IS NULL
            RETURNING c."Id" AS "Value"
            """).ToListAsync(cancellationToken);

        var announced = conversationIds.Select(id => (ConversationId: id, Row: outboxFor(id))).ToList();
        _db.OutboxMessages.AddRange(announced.Select(a => a.Row));
        await _db.SaveChangesAsync(cancellationToken);
        return announced;
    }
}
