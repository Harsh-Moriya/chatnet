using ChatNET.API.Auth.Models;
using ChatNET.API.Common.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Auth.Persistence;

public class RefreshTokenRepository : IRefreshTokenRepository
{
    private readonly AppDbContext _db;

    public RefreshTokenRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(RefreshToken token, CancellationToken cancellationToken)
    {
        _db.RefreshTokens.Add(token);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> AddIfSecurityStampUnchangedAsync(
        RefreshToken token, string? expectedSecurityStamp, CancellationToken cancellationToken)
    {
        // Registration calls this inside a transaction of its own; a sign-in has none, so it gets one here. The lock is held
        // until the transaction ends either way.
        await using var own = _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        if (!await LockUserAndCompareStampAsync(token.UserId, expectedSecurityStamp, cancellationToken))
            return false;

        _db.RefreshTokens.Add(token);
        await _db.SaveChangesAsync(cancellationToken);

        if (own is not null)
            await own.CommitAsync(cancellationToken);

        return true;
    }

    public Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken)
        => _db.RefreshTokens
            .AsNoTracking()
            .Include(t => t.User)
            .SingleOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

    public async Task<bool> TryRotateAsync(
        RefreshToken current, RefreshToken successor, DateTime now, CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        // A password reset locks the person's row before it revokes their tokens. Holding a share lock on that row from here to the
        // commit makes the two take turns: the reset either waits for this successor to exist and then revokes it too, or goes
        // first, and the update below finds the current token revoked and creates nothing.
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"AspNetUsers\" WHERE \"Id\" = {current.UserId} FOR SHARE", cancellationToken);

        // One statement does the check and the change. Two requests that refresh the same token at the
        // same moment both reach it; Postgres makes the second wait for the first to finish, looks at
        // the row again, finds it already retired, and updates nothing.
        var retired = await _db.RefreshTokens
            .Where(t => t.Id == current.Id && t.RevokedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.RevokedAt, now).SetProperty(t => t.ReplacedById, successor.Id),
                cancellationToken);

        if (retired == 0)
            return false;

        _db.RefreshTokens.Add(successor);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return true;
    }

    public Task RevokeFamilyAsync(Guid familyId, DateTime now, CancellationToken cancellationToken)
        => _db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);

    public Task<int> RevokeAllForUserAsync(Guid userId, DateTime now, CancellationToken cancellationToken)
        => _db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);

    public Task<int> DeleteExpiredBeforeAsync(DateTime expiredBefore, CancellationToken cancellationToken)
        => _db.RefreshTokens
            .Where(t => t.ExpiresAt < expiredBefore)
            .ExecuteDeleteAsync(cancellationToken);

    // Takes a share lock on the person's row and reports whether their security stamp is still the one given. The stamp changes
    // when the password does, so a different one means a reset got in between.
    private async Task<bool> LockUserAndCompareStampAsync(Guid userId, string? expected, CancellationToken cancellationToken)
    {
        var stamps = await _db.Database.SqlQuery<string?>(
                $"SELECT \"SecurityStamp\" AS \"Value\" FROM \"AspNetUsers\" WHERE \"Id\" = {userId} FOR SHARE")
            .ToListAsync(cancellationToken);

        return stamps.Count == 1 && stamps[0] == expected;
    }
}
