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

    // A connection closed. It is removed, entries that expired are dropped, and the result says where
    // that leaves the user ("settling" them). A user with no live connection left starts a wait of
    // "grace" and stays online until it is over, so a page refresh is not announced as offline then
    // online; every close that leaves nobody connected restarts the wait, which is therefore always
    // measured from the last one. With no grace the user is taken offline here. The caller who gets
    // WentOffline is the only one to tell their contacts, however many callers race.
    Task<SettleResult> CloseAsync(
        Guid userId, string connectionId, TimeSpan grace, CancellationToken cancellationToken);

    // For whoever finishes what a close started (a grace timer) or finds a user whose entries all
    // expired (the sweeper). A user with no live connection and no wait running is taken offline, with
    // the same single announcer. While a wait is running the result is NoConnectionsLeft and says how
    // long is left, so a timer that was started by an earlier close does not cut a later wait short.
    Task<SettleResult> FinishAsync(Guid userId, CancellationToken cancellationToken);

    // Users whose newest entry has expired without anyone settling them (a server that died), at
    // most "max". Each is to be settled with FinishAsync.
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

    // They have none left and a wait is running (Wait says how long is left): they stay online until
    // it is over.
    NoConnectionsLeft,

    // Someone else already took them offline, or they were never online.
    AlreadyOffline,
}

// Wait is only set with NoConnectionsLeft.
public readonly record struct SettleResult(SettleOutcome Outcome, DateTime At, TimeSpan Wait = default);

// LastActiveAt is when their newest entry was last renewed, which is as close as Redis can say to
// when they were last seen.
public readonly record struct ExpiredUser(Guid UserId, DateTime LastActiveAt);

public readonly record struct ExpiredUsers(DateTime At, IReadOnlyList<ExpiredUser> Users);

public readonly record struct PresenceSnapshot(DateTime At, IReadOnlySet<Guid> Online);
