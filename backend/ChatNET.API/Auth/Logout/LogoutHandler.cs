using ChatNET.API.Auth.Persistence;
using ChatNET.API.Auth.Services;
using MediatR;

namespace ChatNET.API.Auth.Logout;

// Ends the session the cookie belongs to: every refresh token in its family stops working, so the
// browser (and anyone who copied the token) can no longer get a new access token. An access token that
// is already out lives until its own expiry, which is why those are short.
public class LogoutHandler : IRequestHandler<LogoutCommand, Unit>
{
    private readonly IRefreshTokenRepository _tokens;
    private readonly TimeProvider _time;
    private readonly ILogger<LogoutHandler> _logger;

    public LogoutHandler(IRefreshTokenRepository tokens, TimeProvider time, ILogger<LogoutHandler> logger)
    {
        _tokens = tokens;
        _time = time;
        _logger = logger;
    }

    public async Task<Unit> Handle(LogoutCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.RefreshToken))
            return Unit.Value;

        var token = await _tokens.FindByHashAsync(RefreshTokenIssuer.Hash(request.RefreshToken), cancellationToken);

        // An unknown or already revoked token is a successful sign-out: the outcome asked for is true.
        if (token is null)
            return Unit.Value;

        await _tokens.RevokeFamilyAsync(token.FamilyId, _time.GetUtcNow().UtcDateTime, cancellationToken);

        _logger.LogInformation("User {UserId} signed out; family {FamilyId} revoked", token.UserId, token.FamilyId);

        return Unit.Value;
    }
}
