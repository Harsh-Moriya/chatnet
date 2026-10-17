namespace ChatNET.API.Presence.Persistence;

public interface IPresenceRepository
{
    // Records when a user was last seen, only if that is later than what is stored: events about one
    // user can be handled out of order by different servers, and the stored time must not go back.
    Task SetLastSeenAsync(Guid userId, DateTime lastSeenAt, CancellationToken cancellationToken);

    // When each of these users was last seen going offline; null for one who never has been.
    Task<Dictionary<Guid, DateTime?>> GetLastSeenAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);
}
