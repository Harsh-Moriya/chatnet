import { create } from 'zustand'

// Which file the viewer is showing. A picture in a message opens the viewer from deep inside the message list, so this is a store and not
// a prop passed down through it.
export interface ViewerTarget {
  conversationId: string
  attachmentId: string
}

interface ViewerState {
  target: ViewerTarget | null
  open: (conversationId: string, attachmentId: string) => void
  close: () => void
}

export const useViewerStore = create<ViewerState>()((set) => ({
  target: null,
  open: (conversationId, attachmentId) => set({ target: { conversationId, attachmentId } }),
  // The dialog can ask to close twice (its own event and the effect that closes it), so closing a closed viewer changes nothing.
  close: () => set((state) => (state.target === null ? state : { target: null })),
}))
