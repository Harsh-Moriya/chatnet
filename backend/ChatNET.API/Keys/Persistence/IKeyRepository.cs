using ChatNET.API.Keys.Models;
using ChatNET.API.Outbox.Models;

namespace ChatNET.API.Keys.Persistence;

public enum GenerationOutcome
{
    Added,

    // The person has no keys yet: there is no generation to follow.
    NoKeys,

    // The bundle is not the next generation.
    GenerationStale,

    // The backup moved on since the version the change was made from.
    BackupVersionStale,

    // The device to remove is not one of the person's, or is already removed.
    DeviceNotFound,
}

// What a new generation did: the version the backup now has and the conversations that were marked for a new key period, each with
// the outbox row saved with it.
public sealed record GenerationResult(GenerationOutcome Outcome, int BackupVersion, IReadOnlyList<(Guid ConversationId, OutboxMessage Row)> Conversations)
{
    public static GenerationResult Refused(GenerationOutcome outcome) => new(outcome, 0, []);
}

public interface IKeyRepository
{
    // The public keys of every generation of a person, oldest first.
    Task<List<AccountKeyBundle>> GetBundlesAsync(Guid userId, CancellationToken cancellationToken);

    // The newest generation of each of these people who has keys. People without keys are not in the answer.
    Task<List<AccountKeyBundle>> GetCurrentBundlesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);

    // The newest generation number of each of these people who has keys; people without keys are not in the answer.
    Task<Dictionary<Guid, int>> GetCurrentGenerationsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);

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

    // As GetCurrentGenerationsAsync, for a request that is about to seal a conversation key to these people and has a transaction open:
    // it first takes the shared lock on each person's keys (see KeyGenerationLock), which it holds until that transaction ends, so a
    // new generation cannot be stored between reading the generations and saving the epoch made for them.
    Task<Dictionary<Guid, int>> HoldCurrentGenerationsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);

    // Stores the next generation of a person's account key and the backup that holds it, removes the device that was lost (when one is
    // named) and marks every conversation the person is in for a new key period, all together or not at all. "outboxFor" makes the row
    // that announces a conversation's change, saved with it. The generation must be the one after the newest, the backup is replaced
    // only from the version the caller read, and the device must be one of the person's. Under the lock on the person's keys, so
    // nothing sealed to the old generation is stored after the conversations were marked.
    Task<GenerationResult> TryAddGenerationAsync(
        Guid userId, int generation, byte[] bundle, byte[] backup, int expectedBackupVersion, Guid? removeDeviceId,
        Func<Guid, OutboxMessage> outboxFor, DateTime now, CancellationToken cancellationToken);
}
