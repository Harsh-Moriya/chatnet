namespace ChatNET.API.Presence;

// What the live connections of a user look like in Redis, and the few atomic steps that change it.
// A user is "online" from the moment their contacts are told so until the moment they are told they
// are offline; see PresenceScripts for why that, and not "has a live connection right now", is the
// definition.
//
// Every time in a result is Redis's own clock, not this server's. Servers can disagree by a few
// milliseconds, and "who was first" has to mean the same thing to all of them: it is the order in
// which Redis ran the steps, which is the order in which the state actually changed.
public interface IPresenceStore
{
    // Records that this connection is alive and renews its entry. CameOnline is true when the user was
    // not online before, which is the moment their contacts should be told.
    Task<TouchResult> TouchAsync(Guid userId, string connectionId, CancellationToken cancellationToken);

    // The same for many connections in one round trip, results in the order given.
    Task<IReadOnlyList<TouchResult>> TouchManyAsync(
        IReadOnlyList<(Guid UserId, string ConnectionId)> connections, CancellationToken cancellationToken);

    // Removes one connection (when connectionId is given) and drops entries that expired, then reports
    // where that leaves the user. With finish, a user who has no live connection left is taken
    // offline here and the caller who gets WentOffline is the only one told so, however many callers
    // race. Without finish the user is left in place, so the caller can wait before finishing.
    Task<SettleResult> SettleAsync(
        Guid userId, string? connectionId, bool finish, CancellationToken cancellationToken);

    // Users whose newest entry has expired without anyone settling them (a server that died), at
    // most "max". Each is to be settled with SettleAsync(finish: true).
    Task<ExpiredUsers> GetExpiredAsync(int max, CancellationToken cancellationToken);

    // Which of these users are online, in the sense above, as of the returned time.
    Task<PresenceSnapshot> GetSnapshotAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);
}

public readonly record struct TouchResult(bool CameOnline, DateTime At);

public enum SettleOutcome
{
    // They still have a live connection.
    StillOnline,

    // They have none left and this call took them offline: tell their contacts.
    WentOffline,

    // They have none left and were left in place (finish was false).
    NoConnectionsLeft,

    // Someone else already took them offline, or they were never online.
    AlreadyOffline,
}

public readonly record struct SettleResult(SettleOutcome Outcome, DateTime At);

// LastActiveAt is when their newest entry was last renewed, which is as close as Redis can say to
// when they were last seen.
public readonly record struct ExpiredUser(Guid UserId, DateTime LastActiveAt);

public readonly record struct ExpiredUsers(DateTime At, IReadOnlyList<ExpiredUser> Users);

public readonly record struct PresenceSnapshot(DateTime At, IReadOnlySet<Guid> Online);
