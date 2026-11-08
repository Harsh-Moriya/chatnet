using ChatNET.API.Auth.Models;
using ChatNET.API.Common.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Auth.Persistence;

public class PasswordResetTokenRepository : IPasswordResetTokenRepository
{
    private readonly AppDbContext _db;

    public PasswordResetTokenRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(PasswordResetToken token, CancellationToken cancellationToken)
    {
        _db.PasswordResetTokens.Add(token);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            // A save that failed leaves the row tracked as new, and every later save on this context would try it again and
            // fail the same way. Detaching only this row leaves whatever else the context holds alone.
            _db.Entry(token).State = EntityState.Detached;
        }
    }

    public async Task<Guid?> FindUserIdAsync(string tokenHash, CancellationToken cancellationToken)
        => await _db.PasswordResetTokens
            .AsNoTracking()
            .Where(t => t.TokenHash == tokenHash)
            .Select(t => (Guid?)t.UserId)
            .SingleOrDefaultAsync(cancellationToken);

    // One statement does the check and the change, as the rotation of a refresh token does. Raw SQL because EF cannot express
    // UPDATE ... RETURNING, and the time is a parameter, not the database's clock, so a test can move it.
    public async Task<Guid?> TryConsumeAsync(string tokenHash, DateTime now, CancellationToken cancellationToken)
    {
        var users = await _db.Database.SqlQuery<Guid>($"""
            UPDATE "PasswordResetTokens"
            SET "UsedAt" = {now}
            WHERE "TokenHash" = {tokenHash} AND "UsedAt" IS NULL AND "ExpiresAt" > {now}
            RETURNING "UserId" AS "Value"
            """).ToListAsync(cancellationToken);

        return users.Count == 0 ? null : users[0];
    }

    public Task VoidUnusedAsync(Guid userId, DateTime now, CancellationToken cancellationToken)
        => _db.PasswordResetTokens
            .Where(t => t.UserId == userId && t.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), cancellationToken);

    public Task<int> DeleteExpiredBeforeAsync(DateTime expiredBefore, CancellationToken cancellationToken)
        => _db.PasswordResetTokens
            .Where(t => t.ExpiresAt < expiredBefore)
            .ExecuteDeleteAsync(cancellationToken);
}
