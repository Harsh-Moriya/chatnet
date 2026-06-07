namespace ChatNET.API.Common.Exceptions;

/// <summary>
/// A conflict with existing state (e.g. already a member of a conversation). Maps to HTTP 409.
/// </summary>
public sealed class ConflictException : ChatAppException
{
    public ConflictException(string message) : base(message) { }
}
