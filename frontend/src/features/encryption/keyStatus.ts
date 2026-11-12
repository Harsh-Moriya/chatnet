import { create } from 'zustand'

// Which conversations cannot get the key they need, and why: someone in them has not set up encryption, so nothing can be sealed for
// them and the conversation cannot be written in until they do (or are removed). The chat shows it above the box where a message is
// written. Kept apart from the cryptography, so the screen that shows it does not load it.

interface KeyStatusState {
  // The people who have not set up encryption, by conversation. A conversation that is not here is not held up.
  waitingFor: Record<string, string[]>
  setWaitingFor: (conversationId: string, userIds: string[]) => void
  clear: (conversationId: string) => void
  reset: () => void
}

export const useKeyStatusStore = create<KeyStatusState>()((set) => ({
  waitingFor: {},

  setWaitingFor: (conversationId, userIds) =>
    set((state) => ({ waitingFor: { ...state.waitingFor, [conversationId]: userIds } })),

  clear: (conversationId) =>
    set((state) => {
      if (!(conversationId in state.waitingFor)) return state

      const next = { ...state.waitingFor }
      delete next[conversationId]
      return { waitingFor: next }
    }),

  reset: () => set({ waitingFor: {} }),
}))
