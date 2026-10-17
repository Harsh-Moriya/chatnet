import { create } from 'zustand'
import { compareTimestamps } from '../../../lib/time'
import type { Presence } from '../types'

// Who among the signed-in user's contacts is online, as far as this browser has heard. Two sources
// feed it, the same way messages do: GET /api/presence (a snapshot, loaded at start and after a
// reconnect) and PresenceChanged (pushes). Both go through the same rule, so neither can undo the
// other with older news.

interface PresenceState {
  byUser: Record<string, Presence>

  // Applies one change, unless a newer one about the same person has been applied already.
  apply: (presence: Presence) => void
  // Applies a snapshot: every entry by the same rule.
  seed: (presences: Presence[]) => void
  reset: () => void
}

// The entry that results from applying a change on top of what is known, or the known entry itself
// when the change is older. An online change carries no last-seen time (they are online); the one
// already known is kept, so "last seen" is still there for when they leave again.
function merged(existing: Presence | undefined, incoming: Presence): Presence {
  if (existing && compareTimestamps(incoming.at, existing.at) < 0) return existing

  return { ...incoming, lastSeenAt: incoming.lastSeenAt ?? existing?.lastSeenAt ?? null }
}

export const usePresenceStore = create<PresenceState>()((set) => ({
  byUser: {},

  apply: (presence) =>
    set((state) => {
      const existing = state.byUser[presence.userId]
      const next = merged(existing, presence)

      // The old object when nothing changed, which tells Zustand not to notify anyone.
      return next === existing ? state : { byUser: { ...state.byUser, [presence.userId]: next } }
    }),

  seed: (presences) =>
    set((state) => {
      const byUser = { ...state.byUser }
      for (const presence of presences) byUser[presence.userId] = merged(byUser[presence.userId], presence)
      return { byUser }
    }),

  reset: () => set({ byUser: {} }),
}))
