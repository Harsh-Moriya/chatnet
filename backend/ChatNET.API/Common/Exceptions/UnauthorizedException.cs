namespace ChatNET.API.Common.Exceptions;

/// <summary>
/// Authentication failed — invalid credentials or missing token. Maps to HTTP 401.
/// </summary>
public sealed class UnauthorizedException : ChatAppException
{
    public UnauthorizedException(string message = "Authentication is required.")
        : base(message) { }
}
