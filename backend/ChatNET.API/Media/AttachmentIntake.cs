using ChatNET.API.Messaging.Models;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Options;

namespace ChatNET.API.Media;

// The files a message is about to carry, ready to be saved with it, and the uploads they were made from.
public sealed record StagedAttachments(List<MessageAttachment> Attachments, List<string> PendingKeys);

// An upload the message names is not there for this person: the key was never theirs, the object is gone, or it
// changed after it was looked at. One answer for all three, so a key reveals nothing about anyone else's files.
public sealed class UploadMissingException : ValidationException
{
    public const string Reason = "One of the files was not uploaded.";

    public UploadMissingException() : base([new ValidationFailure("Attachments", Reason)])
    {
    }
}

// Turns the uploads a message names into attachments. A presigned PUT cannot cap the size or inspect what is
// sent, so everything is checked here, on the object that was really stored: it must exist under a key made for
// this person in this conversation, be within the limits and of an allowed type. Only then is it copied to its
// permanent place, inside the storage, without the bytes passing through the server.
public sealed class AttachmentIntake
{
    private readonly IBlobStorage _storage;
    private readonly StorageOptions _options;
    private readonly ILogger<AttachmentIntake> _logger;

    public AttachmentIntake(IBlobStorage storage, IOptions<StorageOptions> options, ILogger<AttachmentIntake> logger)
    {
        _storage = storage;
        _options = options.Value;
        _logger = logger;
    }

    // Throws a validation error when a file may not be attached, after removing the copies already made for the
    // message, so a refused send leaves nothing behind except uploads under pending/.
    public async Task<StagedAttachments> StageAsync(
        Guid conversationId,
        Guid senderId,
        IReadOnlyList<AttachmentInput> inputs,
        DateTime createdAt,
        CancellationToken cancellationToken)
    {
        var attachments = new List<MessageAttachment>();
        var pendingKeys = new List<string>();

        try
        {
            for (var position = 0; position < inputs.Count; position++)
            {
                var input = inputs[position];
                var uploadKey = input.UploadKey!;

                if (!AttachmentKeys.IsUploadKeyFor(uploadKey, conversationId, senderId))
                    throw new UploadMissingException();

                var stored = await _storage.HeadAsync(uploadKey, cancellationToken)
                    ?? throw new UploadMissingException();

                var contentType = AttachmentKinds.Normalize(stored.ContentType ?? string.Empty);
                if (stored.SizeBytes <= 0)
                    throw await RefuseAsync(uploadKey, "A file is empty.");
                if (stored.SizeBytes > _options.MaxFileBytes)
                    throw await RefuseAsync(uploadKey, "A file is larger than the limit.");
                if (!_options.Allows(contentType))
                    throw await RefuseAsync(uploadKey, "A file has a type that cannot be sent.");

                var attachmentId = Guid.CreateVersion7();
                var storageKey = AttachmentKeys.StoredKey(conversationId, attachmentId);

                try
                {
                    await _storage.CopyAsync(uploadKey, storageKey, stored.ETag, cancellationToken);
                }
                catch (BlobUnavailableException)
                {
                    throw new UploadMissingException();
                }

                pendingKeys.Add(uploadKey);
                attachments.Add(new MessageAttachment
                {
                    Id = attachmentId,
                    StorageKey = storageKey,
                    Type = AttachmentKinds.TypeOf(contentType),
                    FileName = AttachmentNames.Clean(input.FileName!),
                    ContentType = contentType,
                    FileSizeBytes = stored.SizeBytes,

                    // One microsecond after the file before it: the order they were attached in (see MessageAttachment).
                    CreatedAt = createdAt.AddTicks(position * 10),
                });
            }
        }
        catch
        {
            await DiscardAsync(attachments);
            throw;
        }

        return new StagedAttachments(attachments, pendingKeys);
    }

    // Removes the permanent copies of attachments that were never saved. Used when the message was not stored.
    public async Task DiscardAsync(IEnumerable<MessageAttachment> attachments)
    {
        foreach (var attachment in attachments)
            await DeleteQuietlyAsync(attachment.StorageKey);
    }

    // Removes the uploads a stored message has used. The bucket's lifecycle rule would remove them within a day;
    // this only stops them waiting for it.
    public async Task ReleaseAsync(IEnumerable<string> pendingKeys)
    {
        foreach (var key in pendingKeys)
            await DeleteQuietlyAsync(key);
    }

    // An upload that broke a rule is deleted so that it does not stay in the bucket under the person's folder.
    private async Task<ValidationException> RefuseAsync(string uploadKey, string reason)
    {
        await DeleteQuietlyAsync(uploadKey);
        return new ValidationException([new ValidationFailure("Attachments", reason)]);
    }

    // Cleaning up must not turn a result that is already decided into an error, and must run even when the request
    // has been cancelled, so it ignores the request's token and swallows what the storage says.
    private async Task DeleteQuietlyAsync(string key)
    {
        try
        {
            await _storage.DeleteAsync(key, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete the stored object {StorageKey}; it stays until the bucket's lifecycle rule or a clean-up removes it", key);
        }
    }
}
