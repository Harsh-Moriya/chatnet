using ChatNET.API.Auth.Services;

namespace ChatNET.API.Auth.Models;

// The body of a successful sign-in, registration or refresh. The refresh token is not part of it: it
// travels only in an httpOnly cookie, which scripts on the page cannot read.
public record AuthResponse(
    string Token,
    string UserId,
    string UserName,
    string DisplayName,
    DateTime AccessTokenExpiresAt
)
{
    public static AuthResponse For(AppUser user, AccessToken accessToken)
        => new(accessToken.Value, user.Id.ToString(), user.UserName!, user.DisplayName, accessToken.ExpiresAt);
}
