using ChatNET.API.Auth.Models;
using ChatNET.API.Outbox.Models;
using ChatNET.API.Users.SearchUsers;

namespace ChatNET.API.Users.Persistence;

public interface IProfileRepository
{
    // The person as others see them (picture as an address), or null for an id that is not a user.
    Task<UserSummaryDto?> GetSummaryAsync(Guid userId, CancellationToken cancellationToken);

    // Whether any user or group has this stored picture as theirs right now.
    Task<bool> IsAvatarKeyInUseAsync(string key, CancellationToken cancellationToken);

    // Starts a change to one person's profile under a lock on that person; null for an id that is not a user.
    Task<IProfileChange?> BeginProfileChangeAsync(Guid userId, CancellationToken cancellationToken);
}

// One change to a person's profile, made while holding that person's lock: a second change to the same profile waits until
// this one is committed or disposed. That makes User the current row for as long as the change lasts, so the picture it
// replaces is exactly the one that is replaced, and no stored picture is left without an owner by two changes made together.
//
// Disposing without committing discards the change and frees the lock.
public interface IProfileChange : IAsyncDisposable
{
    // The person as they are now, tracked, so a new name or picture is a change to this object.
    AppUser User { get; }

    // Saves what was changed together with the outbox rows that belong to it, in one transaction, and frees the lock.
    Task CommitAsync(IReadOnlyList<OutboxMessage> outbox, CancellationToken cancellationToken);
}
