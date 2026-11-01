using FluentValidation;
using FluentValidation.Results;

namespace ChatNET.API.Media;

// Turns an upload into a picture. A presigned PUT cannot cap the size or inspect what is sent, so everything is checked
// here, on the object that was really stored: it must exist under a key made for this owner, be within the limit and of an
// allowed type. Only then is it copied to its permanent place, inside the storage, without the bytes passing through the
// server.
public sealed class AvatarIntake
{
    private const string Field = "UploadKey";

    private readonly IBlobStorage _storage;
    private readonly ILogger<AvatarIntake> _logger;

    public AvatarIntake(IBlobStorage storage, ILogger<AvatarIntake> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    // The stored key of the new picture. Throws a validation error when the upload cannot become a picture; the upload is
    // removed when it broke a rule, so it does not stay under the owner's folder. One answer for a key that was never this
    // owner's, an object that is gone and one that changed, so a key reveals nothing about anyone else's uploads.
    public async Task<string> AdoptAsync(AvatarOwner owner, Guid ownerId, string uploadKey, CancellationToken cancellationToken)
    {
        if (!AvatarKeys.IsUploadKeyFor(uploadKey, owner, ownerId))
            throw Refusal("The picture was not uploaded.");

        var stored = await _storage.HeadAsync(uploadKey, cancellationToken)
            ?? throw Refusal("The picture was not uploaded.");

        if (stored.SizeBytes <= 0)
            throw await RefuseAndRemoveAsync(uploadKey, "The picture is empty.");
        if (stored.SizeBytes > AvatarRules.MaxBytes)
            throw await RefuseAndRemoveAsync(uploadKey, "The picture is larger than the limit.");
        if (AvatarRules.Match(stored.ContentType ?? string.Empty) is null)
            throw await RefuseAndRemoveAsync(uploadKey, "The picture has a type that cannot be used.");

        var key = AvatarKeys.StoredKey(owner, ownerId, Guid.CreateVersion7());

        try
        {
            await _storage.CopyAsync(uploadKey, key, stored.ETag, cancellationToken);
        }
        catch (BlobUnavailableException)
        {
            throw Refusal("The picture was not uploaded.");
        }

        await DeleteQuietlyAsync(uploadKey);
        return key;
    }

    // Removes a permanent copy that was never saved as anyone's picture, because the change failed after it was made.
    public Task DiscardAsync(string key) => DeleteQuietlyAsync(key);

    private static ValidationException Refusal(string reason)
        => new([new ValidationFailure(Field, reason)]);

    private async Task<ValidationException> RefuseAndRemoveAsync(string uploadKey, string reason)
    {
        await DeleteQuietlyAsync(uploadKey);
        return Refusal(reason);
    }

    // Cleaning up must not turn a result that is already decided into an error, and must run even when the request has been
    // cancelled, so it ignores the request's token and swallows what the storage says.
    private async Task DeleteQuietlyAsync(string key)
    {
        try
        {
            await _storage.DeleteAsync(key, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete a stored picture object; it stays until the bucket's lifecycle rule removes it");
        }
    }
}
