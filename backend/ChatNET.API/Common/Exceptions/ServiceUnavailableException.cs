namespace ChatNET.API.Common.Exceptions;

/// <summary>
/// A feature this server has not been set up for, so it cannot do what was asked now. Maps to HTTP 503.
/// </summary>
public sealed class ServiceUnavailableException : ChatNETException
{
    public ServiceUnavailableException(string message)
        : base(message) { }
}
