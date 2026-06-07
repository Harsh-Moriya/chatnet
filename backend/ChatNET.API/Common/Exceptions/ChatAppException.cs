namespace ChatNET.API.Common.Exceptions;

/// <summary>
/// Base exception for all expected domain failures in ChatNET.
/// Never throw this directly — use a specific subtype so the global
/// exception handler can map it to the correct HTTP status code.
/// </summary>
public abstract class ChatAppException : Exception
{
    protected ChatAppException(string message) : base(message) { }
}
