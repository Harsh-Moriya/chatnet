// One person's presence as the API and the live connection describe it (see section 6.3 of the
// architecture overview). The same shape comes from GET /api/presence and from PresenceChanged.
export interface Presence {
  userId: string
  isOnline: boolean
  // When they were last seen going offline. Null for someone who never has been; an online change
  // carries none, and the store keeps the one it already knows.
  lastSeenAt: string | null
  // The time of the change this describes, on the server's Redis clock, or of the snapshot: used to drop
  // a message that arrives after a newer one.
  at: string
}
