using ChatNET.API.Media;

namespace ChatNET.API.Tests.Media;

// Test double for a storage that answers at once. Every key looks like a small stored image and copying and
// deleting do nothing. With a real storage each file takes a round trip, so the files of a message are looked at
// milliseconds apart; with this one they are looked at within microseconds, which is what exposes anything that
// quietly depends on time passing between them.
public sealed class InstantBlobStorage : IBlobStorage
{
    public SignedUrl CreateUploadUrl(string key, string contentType) => Link(key);

    public SignedUrl CreateDownloadUrl(string key, string fileName, bool inline) => Link(key);

    public Task<StoredObject?> HeadAsync(string key, CancellationToken cancellationToken)
        => Task.FromResult<StoredObject?>(new StoredObject(100, "image/png", "\"etag\""));

    public Task CopyAsync(string sourceKey, string destinationKey, string sourceETag, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task<BlobContent?> OpenReadAsync(string key, CancellationToken cancellationToken)
        => Task.FromResult<BlobContent?>(null);

    public Task DeleteAsync(string key, CancellationToken cancellationToken) => Task.CompletedTask;

    private static SignedUrl Link(string key) => new($"https://files.test/{key}", DateTimeOffset.UtcNow.AddMinutes(10));
}
