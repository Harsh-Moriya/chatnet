using System.Security.Cryptography;
using ChatNET.API.Auth.Models;
using ChatNET.API.Auth.Persistence;
using ChatNET.API.Common.Exceptions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Auth.Services;

// Makes refresh tokens. A token is 32 random bytes (256 bits); the stored copy is its hash (see TokenHash).
public class RefreshTokenIssuer
{
    private readonly IRefreshTokenRepository _tokens;
    private readonly JwtOptions _options;
    private readonly TimeProvider _time;

    public RefreshTokenIssuer(IRefreshTokenRepository tokens, IOptions<JwtOptions> options, TimeProvider time)
    {
        _tokens = tokens;
        _options = options.Value;
        _time = time;
    }

    // Starts a new family, for a sign-in or a registration, and stores its first token. The person was found and their password
    // checked a moment ago, with the security stamp they have now; if a password reset has changed the stamp since, the password
    // that was checked is no longer the password, and no session is started.
    public async Task<IssuedRefreshToken> StartSessionAsync(AppUser user, CancellationToken cancellationToken, Guid? deviceId = null)
    {
        var (entity, issued) = Create(user.Id, Guid.NewGuid(), _time.GetUtcNow().UtcDateTime, deviceId);

        if (!await _tokens.AddIfSecurityStampUnchangedAsync(entity, user.SecurityStamp, cancellationToken))
            throw new UnauthorizedException("Invalid email or password.");

        return issued;
    }

    // The token that replaces another in the same family. Not stored: the caller stores it together with
    // retiring the one it replaces.
    public (RefreshToken Entity, IssuedRefreshToken Issued) CreateSuccessor(RefreshToken current, DateTime now, Guid? deviceId)
        => Create(current.UserId, current.FamilyId, now, deviceId);

    private (RefreshToken Entity, IssuedRefreshToken Issued) Create(Guid userId, Guid familyId, DateTime now, Guid? deviceId)
    {
        var value = TokenHash.NewToken();
        var expiresAt = now.AddDays(_options.RefreshTokenDays);
        var hash = TokenHash.Of(value);

        var entity = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            FamilyId = familyId,
            DeviceId = deviceId,
            TokenHash = hash,
            CreatedAt = now,
            ExpiresAt = expiresAt,
        };

        return (entity, new IssuedRefreshToken(value, expiresAt));
    }
}
