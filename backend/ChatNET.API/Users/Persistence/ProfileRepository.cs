using ChatNET.API.Common.Persistence;
using ChatNET.API.Media;
using ChatNET.API.Users.SearchUsers;
using Microsoft.EntityFrameworkCore;

namespace ChatNET.API.Users.Persistence;

public class ProfileRepository : IProfileRepository
{
    private readonly AppDbContext _db;

    public ProfileRepository(AppDbContext db)
    {
        _db = db;
    }

    public Task<UserSummaryDto?> GetSummaryAsync(Guid userId, CancellationToken cancellationToken)
        => _db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new UserSummaryDto(u.Id, u.UserName!, u.DisplayName, AvatarKeys.PublicUrl(u.AvatarKey)))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<bool> IsAvatarKeyInUseAsync(string key, CancellationToken cancellationToken)
        => await _db.Users.AnyAsync(u => u.AvatarKey == key, cancellationToken)
           || await _db.Conversations.AnyAsync(c => c.AvatarKey == key, cancellationToken);

    // The same lock as registration and membership changes take, on a different value: a transaction-scoped advisory lock
    // on the person, released by the commit or the rollback, so a request that fails cannot leave it held.
    public async Task<IProfileChange?> BeginProfileChangeAsync(Guid userId, CancellationToken cancellationToken)
    {
        var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({"profile:" + userId}, 0))", cancellationToken);

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
            if (user is null)
            {
                await transaction.DisposeAsync();
                return null;
            }

            return new ProfileChange(_db, transaction, user);
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }
}
