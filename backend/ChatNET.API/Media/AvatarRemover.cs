using ChatNET.API.Users.Persistence;

namespace ChatNET.API.Media;

// Removes a picture that was replaced or taken away from the bucket. Used by the request that changed the picture, right
// after the commit, and by the outbox relay when that did not work (see OutboxDispatcher). Deleting an object that is already
// gone succeeds, so a retry is safe.
public sealed class AvatarRemover
{
    private readonly IBlobStorage _storage;
    private readonly IProfileRepository _profiles;
    private readonly ILogger<AvatarRemover> _logger;

    public AvatarRemover(IBlobStorage storage, IProfileRepository profiles, ILogger<AvatarRemover> logger)
    {
        _storage = storage;
        _profiles = profiles;
        _logger = logger;
    }

    // A key that is somebody's picture now is never removed, whatever put it here: a row that is retried after the
    // picture was set again must not take away a picture people can see.
    public async Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        if (await _profiles.IsAvatarKeyInUseAsync(key, cancellationToken))
        {
            _logger.LogWarning("A request to remove a stored picture names one that is in use; nothing removed");
            return;
        }

        await _storage.DeleteAsync(key, cancellationToken);
    }
}
