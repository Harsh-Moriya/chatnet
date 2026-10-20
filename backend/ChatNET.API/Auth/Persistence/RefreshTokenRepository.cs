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

    public Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken)
        => _db.RefreshTokens
            .AsNoTracking()
            .Include(t => t.User)
            .SingleOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

    public async Task<bool> TryRotateAsync(
        RefreshToken current, RefreshToken successor, DateTime now, CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

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

    public Task<int> DeleteExpiredBeforeAsync(DateTime expiredBefore, CancellationToken cancellationToken)
        => _db.RefreshTokens
            .Where(t => t.ExpiresAt < expiredBefore)
            .ExecuteDeleteAsync(cancellationToken);
}
