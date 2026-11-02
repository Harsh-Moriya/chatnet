import { useCallback, useRef, useState } from 'react'
import { getApiError } from '../../../lib/errors'
import { loadNewerInWindow, loadOlderInWindow } from '../api/chatApi'
import type { ReadingMore } from '../history'
import { useChatStore } from '../store/chatStore'

type Direction = 'older' | 'newer'

const IDLE = { loading: false, error: null as string | null }

// Reading further in both directions from a window of a conversation's history (chatStore.windows): the page of older messages
// above it, and the page of newer ones below it, which ends at the newest message. Only one request per direction at a time, and
// while one has failed nothing is requested by itself, so a server that keeps refusing is not asked again in a loop; the
// reader's "Try again" asks once more.
export function useWindowPaging(conversationId: string): { older: ReadingMore; newer: ReadingMore } {
  const window = useChatStore((state) => state.windows[conversationId])
  const [older, setOlder] = useState(IDLE)
  const [newer, setNewer] = useState(IDLE)
  const busy = useRef({ older: false, newer: false })

  const load = useCallback(
    async (direction: Direction) => {
      if (busy.current[direction]) return
      busy.current[direction] = true

      const set = direction === 'older' ? setOlder : setNewer
      set({ loading: true, error: null })

      try {
        await (direction === 'older' ? loadOlderInWindow(conversationId) : loadNewerInWindow(conversationId))
        set(IDLE)
      } catch (failure) {
        set({ loading: false, error: getApiError(failure) })
      } finally {
        busy.current[direction] = false
      }
    },
    [conversationId],
  )

  const loadOlder = useCallback(() => void load('older'), [load])
  const loadNewer = useCallback(() => void load('newer'), [load])

  return {
    older: { hasMore: window?.hasOlder ?? false, isLoading: older.loading, error: older.error, onLoad: loadOlder },
    newer: { hasMore: window?.buffer != null, isLoading: newer.loading, error: newer.error, onLoad: loadNewer },
  }
}
