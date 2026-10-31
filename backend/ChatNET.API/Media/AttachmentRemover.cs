using ChatNET.API.Messaging.Models;

namespace ChatNET.API.Media;

// Removes the files of a deleted message from the bucket. Used by the request that deleted the message, right after
// the commit, and by the outbox relay when that did not work (see OutboxDispatcher).
//
// Every file is tried even when one fails, and the failures are thrown together at the end so the caller knows the
// job is not done and retries it. A retry is safe: deleting an object that is already gone succeeds.
public sealed class AttachmentRemover
{
    private readonly IBlobStorage _storage;
    private readonly ILogger<AttachmentRemover> _logger;

    public AttachmentRemover(IBlobStorage storage, ILogger<AttachmentRemover> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    public async Task RemoveAsync(IReadOnlyCollection<MessageAttachment> files, CancellationToken cancellationToken)
    {
        List<Exception> failures = [];

        foreach (var file in files)
        {
            try
            {
                await _storage.DeleteAsync(file.StorageKey, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // The attachment's id, never its key: the key names a file in the bucket.
                _logger.LogWarning(ex,
                    "Could not delete the stored file of attachment {AttachmentId} of message {MessageId}",
                    file.Id, file.MessageId);

                failures.Add(ex);
            }
        }

        if (failures.Count > 0)
            throw new AggregateException($"{failures.Count} of {files.Count} files could not be deleted.", failures);
    }
}
