namespace ChatNET.API.Presence;

// One person's presence as a contact sees it. The same shape is pushed (PresenceChanged) and returned
// by GET /api/presence, so a client applies both the same way.
//
// At is the time, on Redis's clock, of the change this describes (or of the snapshot it was read
// from), so a client can drop a push that arrives after a newer one. LastSeenAt is when they were
// last seen going offline: null until they have; it is not updated while they are online.
public record PresenceDto(
    Guid UserId,
    bool IsOnline,
    DateTime? LastSeenAt,
    DateTime At
);
