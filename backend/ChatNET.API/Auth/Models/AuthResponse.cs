using ChatNET.API.Auth.Services;

namespace ChatNET.API.Auth.Models;

// Access credential and identity fields shared by the browser and native session responses.
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
