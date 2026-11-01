namespace ChatNET.API.Media;

// A link and the moment it stops working.
public sealed record SignedUrl(string Url, DateTimeOffset ExpiresAt);

// What the storage reports about an object without sending it: the size and type it was stored with, and the
// ETag that names this exact content.
public sealed record StoredObject(long SizeBytes, string ContentType, string ETag);

// The few things the application needs from object storage. Keys are paths inside the one configured bucket.
public interface IBlobStorage
{
    // A link the browser can PUT a file to, straight into the bucket. The content type is part of what is
    // signed, so the storage refuses an upload that claims another one.
    SignedUrl CreateUploadUrl(string key, string contentType);

    // A link the browser can GET the object from. A file that is not an image or a video is served as a
    // download under its own name instead of being opened in the page.
    SignedUrl CreateDownloadUrl(string key, string fileName, bool inline);

    // Null when there is no such object.
    Task<StoredObject?> HeadAsync(string key, CancellationToken cancellationToken);

    // Copies inside the bucket, on the storage's side. It only copies the content that carries "sourceETag", so
    // an object replaced after it was looked at is not copied. Throws BlobUnavailableException when the source is
    // gone or has changed.
    Task CopyAsync(string sourceKey, string destinationKey, string sourceETag, CancellationToken cancellationToken);

    // The object itself, to be sent on by the server. Null when there is no such object.
    Task<BlobContent?> OpenReadAsync(string key, CancellationToken cancellationToken);

    // Removing an object that is not there is not an error.
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}

// An object being read: its bytes and what it was stored with. Disposing it closes the connection to the storage, so the
// reader disposes it when it has sent the bytes.
public sealed class BlobContent : IDisposable
{
    private readonly IDisposable _source;

    public BlobContent(Stream content, string contentType, long length, IDisposable source)
    {
        Content = content;
        ContentType = contentType;
        Length = length;
        _source = source;
    }

    public Stream Content { get; }
    public string ContentType { get; }
    public long Length { get; }

    public void Dispose() => _source.Dispose();
}
