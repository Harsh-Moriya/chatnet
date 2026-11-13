namespace ChatNET.API.Common.Exceptions;

/// <summary>
/// The caller is not authorised to perform this action. Maps to HTTP 403.
/// </summary>
public sealed class ForbiddenException : ChatNETException
{
    public ForbiddenException(string message = "You do not have permission to perform this action.", string? code = null)
        : base(message)
    {
        Code = code;
    }

    /// <summary>
    /// A short, stable name for why the caller was refused, written to the ProblemDetails as "code" so a client can tell a refusal it
    /// can act on (a wrong password) from one it cannot. Null for an ordinary refusal.
    /// </summary>
    public string? Code { get; }
}
