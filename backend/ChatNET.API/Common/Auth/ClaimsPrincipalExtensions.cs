using System.Security.Claims;
using ChatNET.API.Common.Exceptions;

namespace ChatNET.API.Common.Auth;

public static class ClaimsPrincipalExtensions
{
    // The JWT carries the user ID in the standard "sub" claim (see JwtTokenService).
    // Endpoints read it here and put it into the command, so a client can never
    // act as another user by sending a different ID in the request body.
    public static Guid GetUserId(this ClaimsPrincipal principal)
        => principal.TryGetUserId(out var id)
            ? id
            : throw new UnauthorizedException("Token does not contain a valid user ID.");

    public static bool TryGetUserId(this ClaimsPrincipal? principal, out Guid userId)
    {
        var value = principal?.FindFirstValue("sub")
                    ?? principal?.FindFirstValue(ClaimTypes.NameIdentifier);

        return Guid.TryParse(value, out userId);
    }
}
