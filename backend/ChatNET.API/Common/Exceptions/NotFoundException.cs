namespace ChatNET.API.Common.Exceptions;

/// <summary>
/// The requested resource does not exist. Maps to HTTP 404.
/// </summary>
public sealed class NotFoundException : ChatAppException
{
    public NotFoundException(string resource, object id)
        : base($"{resource} with ID '{id}' was not found.") { }
}
