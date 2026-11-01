using ChatNET.API.Media;

namespace ChatNET.API.Tests.Media;

// Test double for the gap between looking at an uploaded object and copying it: right after the real storage
// has reported on an object, "afterHead" runs, and a test uses it to replace the object, as someone holding the
// upload link could. Everything else is the real storage.
public sealed class ReplacingBlobStorage : IBlobStorage
{
    private readonly IBlobStorage _inner;
    private readonly Func<string, Task> _afterHead;

    public ReplacingBlobStorage(IBlobStorage inner, Func<string, Task> afterHead)
    {
        _inner = inner;
        _afterHead = afterHead;
    }

    public async Task<StoredObject?> HeadAsync(string key, CancellationToken cancellationToken)
    {
        var stored = await _inner.HeadAsync(key, cancellationToken);
        await _afterHead(key);
        return stored;
    }

    public SignedUrl CreateUploadUrl(string key, string contentType) => _inner.CreateUploadUrl(key, contentType);

    public SignedUrl CreateDownloadUrl(string key, string fileName, bool inline) =>
        _inner.CreateDownloadUrl(key, fileName, inline);

    public Task CopyAsync(string sourceKey, string destinationKey, string sourceETag, CancellationToken cancellationToken)
        => _inner.CopyAsync(sourceKey, destinationKey, sourceETag, cancellationToken);

    public Task<BlobContent?> OpenReadAsync(string key, CancellationToken cancellationToken)
        => _inner.OpenReadAsync(key, cancellationToken);

    public Task DeleteAsync(string key, CancellationToken cancellationToken) => _inner.DeleteAsync(key, cancellationToken);
}
