using System.Security.Cryptography;
using System.Text;
using ChatNET.API.Auth.Models;
using ChatNET.API.Auth.Persistence;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Auth.Services;

// Makes refresh tokens. A token is 32 random bytes (256 bits), which cannot be guessed, so the stored
// copy is a plain SHA-256 hash: a slow password hash would protect nothing extra here and would add
// cost to every refresh.
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

    public static string Hash(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    // Starts a new family, for a sign-in or a registration, and stores its first token.
    public async Task<IssuedRefreshToken> StartSessionAsync(Guid userId, CancellationToken cancellationToken)
    {
        var (entity, issued) = Create(userId, Guid.NewGuid(), _time.GetUtcNow().UtcDateTime);
        await _tokens.AddAsync(entity, cancellationToken);

        return issued;
    }

    // The token that replaces another in the same family. Not stored: the caller stores it together with
    // retiring the one it replaces.
    public (RefreshToken Entity, IssuedRefreshToken Issued) CreateSuccessor(RefreshToken current, DateTime now)
        => Create(current.UserId, current.FamilyId, now);

    private (RefreshToken Entity, IssuedRefreshToken Issued) Create(Guid userId, Guid familyId, DateTime now)
    {
        var value = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var expiresAt = now.AddDays(_options.RefreshTokenDays);

        var entity = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            FamilyId = familyId,
            TokenHash = Hash(value),
            CreatedAt = now,
            ExpiresAt = expiresAt,
        };

        return (entity, new IssuedRefreshToken(value, expiresAt));
    }
}
