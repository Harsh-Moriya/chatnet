using System.Collections.Concurrent;
using ChatNET.API.Media;

namespace ChatNET.API.Tests.Media;

// Test double for a storage that answers at once. Every key looks like a small stored file and copying and
// deleting do nothing. With a real storage each file takes a round trip, so the files of a message are looked at
// milliseconds apart; with this one they are looked at within microseconds, which is what exposes anything that
// quietly depends on time passing between them. It remembers where each upload was copied to, which is what a link names.
public sealed class InstantBlobStorage : IBlobStorage
{
    private readonly ConcurrentDictionary<string, string> _copies = new();

    // The link a file is given once it is attached, for the upload it was made from.
    public string LinkOf(string uploadKey) => Link(_copies[uploadKey]).Url;

    public SignedUrl CreateUploadUrl(string key, string contentType) => Link(key);

    public SignedUrl CreateDownloadUrl(string key) => Link(key);

    public Task<StoredObject?> HeadAsync(string key, CancellationToken cancellationToken)
        => Task.FromResult<StoredObject?>(new StoredObject(100, "application/octet-stream", "\"etag\""));

    public Task CopyAsync(string sourceKey, string destinationKey, string sourceETag, CancellationToken cancellationToken)
    {
        _copies[sourceKey] = destinationKey;
        return Task.CompletedTask;
    }

    public Task<BlobContent?> OpenReadAsync(string key, CancellationToken cancellationToken)
        => Task.FromResult<BlobContent?>(null);

    public Task DeleteAsync(string key, CancellationToken cancellationToken) => Task.CompletedTask;

    private static SignedUrl Link(string key) => new($"https://files.test/{key}", DateTime.UtcNow.AddMinutes(10));
}
