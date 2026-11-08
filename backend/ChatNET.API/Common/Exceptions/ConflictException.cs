namespace ChatNET.API.Common.Exceptions;

/// <summary>
/// A conflict with existing state (e.g. already a member of a conversation). Maps to HTTP 409.
/// </summary>
public sealed class ConflictException : ChatNETException
{
    public ConflictException(string message, string? code = null) : base(message)
    {
        Code = code;
    }

    /// <summary>
    /// A short, stable name for the kind of conflict, written to the ProblemDetails as "code" so a client can tell conflicts
    /// apart without reading the message. Null for a conflict the client has no separate way to handle.
    /// </summary>
    public string? Code { get; }
}
