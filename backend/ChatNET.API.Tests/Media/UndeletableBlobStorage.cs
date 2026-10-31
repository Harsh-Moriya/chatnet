using ChatNET.API.Media;

namespace ChatNET.API.Tests.Media;

// Test double for a storage that refuses to delete: everything else is the real storage. A deleted message has
// to be deleted whatever the storage says about its files. With a predicate it refuses only the keys it matches.
public sealed class UndeletableBlobStorage : IBlobStorage
{
    private readonly IBlobStorage _inner;
    private readonly Func<string, bool> _refuses;

    public UndeletableBlobStorage(IBlobStorage inner, Func<string, bool>? refuses = null)
    {
        _inner = inner;
        _refuses = refuses ?? (_ => true);
    }

    public SignedUrl CreateUploadUrl(string key, string contentType) => _inner.CreateUploadUrl(key, contentType);

    public SignedUrl CreateDownloadUrl(string key, string fileName, bool inline) =>
        _inner.CreateDownloadUrl(key, fileName, inline);

    public Task<StoredObject?> HeadAsync(string key, CancellationToken cancellationToken) =>
        _inner.HeadAsync(key, cancellationToken);

    public Task CopyAsync(string sourceKey, string destinationKey, string sourceETag, CancellationToken cancellationToken) =>
        _inner.CopyAsync(sourceKey, destinationKey, sourceETag, cancellationToken);

    public Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        _refuses(key)
            ? throw new InvalidOperationException("The storage refused to delete.")
            : _inner.DeleteAsync(key, cancellationToken);
}
