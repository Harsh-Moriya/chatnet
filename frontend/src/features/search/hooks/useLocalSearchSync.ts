import { useEffect, useRef } from 'react'
import { openMessagesQuietly, setOpenedSink } from '../../chat/api/openWire'
import { setReopenListener } from '../../chat/api/reopen'
import { useChatStore } from '../../chat/store/chatStore'
import { useAuthStore } from '../../auth/store/authStore'
import { createIngest } from '../ingest'
import { indexedDbLocalStore } from '../store/indexedDbLocalStore'
import { signatureOf } from '../sync/plan'
import { reopenStored } from '../sync/reopenStored'
import { readIntoStore } from '../sync/run'
import { useSyncStatus } from '../sync/status'

// Keeps this device's copy of the messages the person reads, for as long as the chat is open: every message the app opens is kept as it
// is opened, the messages that could not be opened are opened again when their keys arrive, and each conversation is read into the
// store, first its history and then what changed, whenever the list of conversations says it moved.
export function useLocalSearchSync(): void {
  const userId = useAuthStore((state) => state.user?.userId)
  const signature = useChatStore((state) => signatureOf(state.conversations))
  const stop = useRef<AbortController | null>(null)

  useEffect(() => {
    if (userId === undefined) return

    const controller = new AbortController()
    stop.current = controller

    const ingest = createIngest({
      store: indexedDbLocalStore,
      userId: () => useAuthStore.getState().user?.userId,
      onError: (error) => console.warn('Keeping a message in the local store failed:', error instanceof Error ? error.message : 'unknown error'),
    })

    setOpenedSink(ingest.remember)
    setReopenListener((conversationId) => reopenStored(indexedDbLocalStore, userId, conversationId, openMessagesQuietly))

    return () => {
      controller.abort()
      stop.current = null
      setOpenedSink(null)
      setReopenListener(null)
      void ingest.flush()
      useSyncStatus.getState().reset()
    }
  }, [userId])

  useEffect(() => {
    const controller = stop.current
    if (userId === undefined || controller === null || signature === '') return

    // The most recently active first, so what the person is likeliest to look for is read first.
    const listed = Object.values(useChatStore.getState().conversations)
      .sort((a, b) => b.updatedAt.localeCompare(a.updatedAt))
      .map(({ id, changeSeq }) => ({ id, changeSeq }))

    void readIntoStore(userId, listed, controller.signal)
  }, [userId, signature])
}
