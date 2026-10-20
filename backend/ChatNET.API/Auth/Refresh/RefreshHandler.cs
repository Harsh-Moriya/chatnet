using ChatNET.API.Auth.Models;
using ChatNET.API.Auth.Persistence;
using ChatNET.API.Auth.Services;
using ChatNET.API.Common.Exceptions;
using MediatR;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Auth.Refresh;

// Exchanges a refresh token for a new access token and a new refresh token. The old refresh token is
// retired in the same step, so each one works once.
public class RefreshHandler : IRequestHandler<RefreshCommand, AuthResult>
{
    private const string JustRenewed = "The session was just renewed. Try again.";

    private readonly IRefreshTokenRepository _tokens;
    private readonly RefreshTokenIssuer _issuer;
    private readonly JwtTokenService _jwt;
    private readonly JwtOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RefreshHandler> _logger;

    public RefreshHandler(
        IRefreshTokenRepository tokens,
        RefreshTokenIssuer issuer,
        JwtTokenService jwt,
        IOptions<JwtOptions> options,
        TimeProvider time,
        ILogger<RefreshHandler> logger)
    {
        _tokens = tokens;
        _issuer = issuer;
        _jwt = jwt;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    public async Task<AuthResult> Handle(RefreshCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.RefreshToken))
            throw new SessionRejectedException(clearCookie: false);

        var now = _time.GetUtcNow().UtcDateTime;

        var current = await _tokens.FindByHashAsync(RefreshTokenIssuer.Hash(request.RefreshToken), cancellationToken)
                      ?? throw new SessionRejectedException(clearCookie: true);

        if (current.RevokedAt is { } revokedAt)
        {
            // Replaced by a refresh, and presented again at once: a second tab that sent the cookie
            // before the first one's reply arrived. Not theft. The reply that replaced the token has
            // already set the new cookie, so the client only has to try again, and the cookie must be
            // left alone: it is the new one.
            if (current.ReplacedById is not null && now - revokedAt <= TimeSpan.FromSeconds(_options.RefreshReuseGraceSeconds))
                throw new ConflictException(JustRenewed);

            // Replaced a while ago, yet someone holds it and presents it: the token was copied. Both the
            // owner and the thief hold a token from this family and there is no telling which is which,
            // so all of them stop working.
            if (current.ReplacedById is not null)
            {
                await _tokens.RevokeFamilyAsync(current.FamilyId, now, cancellationToken);

                _logger.LogWarning(
                    "A retired refresh token of user {UserId} was presented again; family {FamilyId} revoked",
                    current.UserId, current.FamilyId);
            }

            throw new SessionRejectedException(clearCookie: true);
        }

        if (current.ExpiresAt <= now)
            throw new SessionRejectedException(clearCookie: true);

        // A deactivated person cannot be stopped from using an access token that is already out, but it
        // does not get renewed, which is what limits that to the access token's lifetime.
        if (current.User!.IsDeactivated)
        {
            await _tokens.RevokeFamilyAsync(current.FamilyId, now, cancellationToken);

            _logger.LogInformation(
                "Refresh refused for deactivated user {UserId}; family {FamilyId} revoked",
                current.UserId, current.FamilyId);

            throw new SessionRejectedException(clearCookie: true);
        }

        var (successor, issued) = _issuer.CreateSuccessor(current, now);

        // False means another request replaced this token between the lookup and now: the same two-tabs
        // case as above, one moment earlier.
        if (!await _tokens.TryRotateAsync(current, successor, now, cancellationToken))
            throw new ConflictException(JustRenewed);

        return new AuthResult(AuthResponse.For(current.User, _jwt.GenerateToken(current.User)), issued);
    }
}
