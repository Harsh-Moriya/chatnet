import { create } from 'zustand'

// What the search box says about the store it searches. Nothing here is kept: it is what is true of this tab now.
interface SyncStatusState {
  // This browser cannot keep the store (no IndexedDB, or it is blocked), so there is nothing to search.
  unavailable: boolean
  // How many conversations still have older messages that have not been read into the store: a search finds what is there so far.
  historyPending: number
  markUnavailable: () => void
  setHistoryPending: (count: number) => void
  reset: () => void
}

const initial = { unavailable: false, historyPending: 0 }

export const useSyncStatus = create<SyncStatusState>()((set) => ({
  ...initial,
  markUnavailable: () => set({ unavailable: true }),
  setHistoryPending: (historyPending) => set({ historyPending }),
  reset: () => set(initial),
}))
