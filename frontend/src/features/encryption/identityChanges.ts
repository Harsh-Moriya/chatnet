import { create } from 'zustand'

// The people whose identity key this browser has seen change, and what was marked verified. Kept apart from the cryptography, so the
// screens that show it (a notice in a conversation, the mark beside a name) do not load it.

interface IdentityChangesState {
  // The key just seen, by person: what the person looking is asked to accept or refuse. A person who is not here has not changed.
  changed: Record<string, Uint8Array>
  // Counts the times a verified mark was set or removed, or a changed key accepted, so what reads the marks reads them again.
  marks: number
  note: (userId: string, seen: Uint8Array) => void
  clear: (userId: string) => void
  markedAgain: () => void
  reset: () => void
}

// Whether the mark of a verified person is shown: it is about the key that was remembered, so it is not shown while that person's key has
// changed and the change has not been accepted.
export function shownAsVerified(verified: boolean, changed: Record<string, Uint8Array>, contactId: string | null): boolean {
  return verified && contactId !== null && !(contactId in changed)
}

export const useIdentityChanges = create<IdentityChangesState>()((set) => ({
  changed: {},
  marks: 0,

  note: (userId, seen) => set((state) => ({ changed: { ...state.changed, [userId]: seen } })),

  clear: (userId) =>
    set((state) => {
      if (!(userId in state.changed)) return state

      const next = { ...state.changed }
      delete next[userId]
      return { changed: next }
    }),

  markedAgain: () => set((state) => ({ marks: state.marks + 1 })),

  reset: () => set({ changed: {}, marks: 0 }),
}))
