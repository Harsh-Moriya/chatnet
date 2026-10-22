namespace ChatNET.API.Media;

// The object an operation was about is gone, or is no longer the object that was looked at a moment ago.
public sealed class BlobUnavailableException : Exception
{
    public BlobUnavailableException(string message, Exception inner) : base(message, inner)
    {
    }
}
