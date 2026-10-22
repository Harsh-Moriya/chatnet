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

    // Removing an object that is not there is not an error.
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}
