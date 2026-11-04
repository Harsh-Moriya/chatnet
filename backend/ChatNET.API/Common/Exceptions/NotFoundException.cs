namespace ChatNET.API.Common.Exceptions;

/// <summary>
/// The requested resource does not exist. Maps to HTTP 404.
/// </summary>
public sealed class NotFoundException : ChatNETException
{
    public NotFoundException(string resource, object id)
        : base($"{resource} with ID '{id}' was not found.") { }

    // For a resource that has no id of its own, such as a feature this server does not have switched on.
    public NotFoundException(string message)
        : base(message) { }
}
