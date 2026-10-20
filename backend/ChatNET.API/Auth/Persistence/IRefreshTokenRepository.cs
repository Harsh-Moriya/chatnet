using ChatNET.API.Auth.Models;

namespace ChatNET.API.Auth.Persistence;

public interface IRefreshTokenRepository
{
    Task AddAsync(RefreshToken token, CancellationToken cancellationToken);

    // With its user, so a refresh can build the new access token without a second query.
    Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken);

    // Retires the current token and stores its successor, together or not at all. Returns false when the
    // current token was no longer usable by the time of the update: another request replaced it first.
    // The update is conditional, so of any number of requests refreshing the same token exactly one wins.
    Task<bool> TryRotateAsync(RefreshToken current, RefreshToken successor, DateTime now, CancellationToken cancellationToken);

    // Revokes every token of the family that is still usable.
    Task RevokeFamilyAsync(Guid familyId, DateTime now, CancellationToken cancellationToken);
}
