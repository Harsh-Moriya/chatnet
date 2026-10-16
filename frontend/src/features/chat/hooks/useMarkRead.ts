import { useEffect } from 'react'
import { usePageVisible } from '../../../lib/usePageVisible'
import { useAuth } from '../../auth/hooks/useAuth'
import { markRead } from '../api/chatApi'
import { messageToMarkRead } from '../readState'
import { useChatStore } from '../store/chatStore'

// How long the situation must hold before the server is told. A burst of messages arriving while
// the reader watches becomes one request for the newest message instead of one per message.
const MARK_READ_DELAY_MS = 500

// Tells the server a conversation was read while its newest messages are in front of the reader.
// The decision is messageToMarkRead; this only waits and sends. A failed request is not repeated by
// itself: the count stays, and the next change (a message, the tab coming to the front, scrolling
// down) makes the same decision again.
export function useMarkRead(conversationId: string, atBottom: boolean): void {
  const { user } = useAuth()
  const visible = usePageVisible()
  const unreadCount = useChatStore((state) => state.conversations[conversationId]?.unreadCount ?? 0)
  const messages = useChatStore((state) => state.messages[conversationId])
  const userId = user?.userId

  const target = messageToMarkRead({ visible, atBottom, unreadCount, messages })

  useEffect(() => {
    if (target === null || !userId) return

    const timer = setTimeout(() => {
      markRead(conversationId, target, userId).catch(() => {})
    }, MARK_READ_DELAY_MS)

    return () => clearTimeout(timer)
  }, [conversationId, target, userId])
}
