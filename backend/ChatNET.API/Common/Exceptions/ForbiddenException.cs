namespace ChatNET.API.Common.Exceptions;

/// <summary>
/// The caller is not authorised to perform this action. Maps to HTTP 403.
/// </summary>
public sealed class ForbiddenException : ChatAppException
{
    public ForbiddenException(string message = "You do not have permission to perform this action.")
        : base(message) { }
}
