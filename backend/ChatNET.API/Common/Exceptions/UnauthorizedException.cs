namespace ChatNET.API.Common.Exceptions;

/// <summary>
/// Authentication failed: invalid credentials or missing token. Maps to HTTP 401. Not sealed, so a failure can say more about itself (see SessionRejectedException).
/// </summary>
public class UnauthorizedException : ChatNETException
{
    public UnauthorizedException(string message = "Authentication is required.")
        : base(message) { }
}
