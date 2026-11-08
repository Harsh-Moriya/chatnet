using ChatNET.API.Keys.Models;

namespace ChatNET.API.Keys.Persistence;

public interface IKeyRepository
{
    // The public keys of every generation of a person, oldest first.
    Task<List<AccountKeyBundle>> GetBundlesAsync(Guid userId, CancellationToken cancellationToken);

    Task<bool> HasKeysAsync(Guid userId, CancellationToken cancellationToken);

    // Without the blob, for the state of a person: whether a backup exists and which version.
    Task<KeyBackup?> GetBackupAsync(Guid userId, CancellationToken cancellationToken);

    // Stores the first generation of the keys of a person and their first backup together or not at all, only if they have no
    // keys yet. Two requests setting up together are decided by the key of the table: one stores, the other gets false.
    Task<bool> TryCreateAsync(Guid userId, byte[] bundle, byte[] backup, DateTime now, CancellationToken cancellationToken);

    // Replaces the backup, only if it is still at the version the caller made the new one from. Returns the new version, or
    // null when the version has moved on (or there is no backup). One conditional statement, so of any number of devices
    // changing the passphrase together exactly one wins.
    Task<int?> TryReplaceBackupAsync(Guid userId, byte[] blob, int expectedVersion, DateTime now, CancellationToken cancellationToken);
}
