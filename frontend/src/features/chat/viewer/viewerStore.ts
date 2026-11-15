import { create } from 'zustand'

// Where the files the viewer steps through come from: the messages the conversation has loaded (a file opened from a message), or the
// gallery's list, which reaches back through the whole history (a file opened from the gallery).
export type ViewerFrom = 'chat' | 'gallery'

// Which file the viewer is showing. A picture in a message opens the viewer from deep inside the message list, so this is a store and not
// a prop passed down through it.
export interface ViewerTarget {
  conversationId: string
  attachmentId: string
  from: ViewerFrom
}

interface ViewerState {
  target: ViewerTarget | null
  open: (conversationId: string, attachmentId: string, from?: ViewerFrom) => void
  close: () => void
}

export const useViewerStore = create<ViewerState>()((set) => ({
  target: null,
  open: (conversationId, attachmentId, from = 'chat') => set({ target: { conversationId, attachmentId, from } }),
  // The dialog can ask to close twice (its own event and the effect that closes it), so closing a closed viewer changes nothing.
  close: () => set((state) => (state.target === null ? state : { target: null })),
}))
