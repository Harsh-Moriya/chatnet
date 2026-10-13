import { useEffect, useLayoutEffect, useMemo, useRef } from 'react'
import { AlertCircle, LoaderCircle } from 'lucide-react'
import { Button } from '../../../components/ui/Button'
import { useNow } from '../../../lib/useNow'
import { useAuth } from '../../auth/hooks/useAuth'
import { dismissMessage, retryMessage } from '../api/chatApi'
import { scrollTopAfterPrepend, wasPrepended } from '../history'
import { useChatStore } from '../store/chatStore'
import { buildTimeline } from '../timeline'
import type { Conversation, Message, PendingMessage } from '../types'
import { MessageBubble } from './MessageBubble'

// Stable empty values, so a conversation with nothing in it does not look like a change on every render.
const NO_MESSAGES: Message[] = []
const NO_PENDING: PendingMessage[] = []

// How close to the bottom still counts as "at the bottom", in pixels.
const BOTTOM_THRESHOLD = 96

// How far above the visible area the top of the list starts loading older messages, so they are
// usually there before the reader reaches the top.
const PRELOAD_MARGIN = '200px'

// Reading further back than the latest page.
export interface OlderMessages {
  hasMore: boolean
  isLoading: boolean
  // Why the last attempt failed. While it is set nothing is requested by itself, so a server that
  // keeps refusing is not asked again in a loop; the reader's "Try again" asks once more.
  error: string | null
  onLoad: () => void
}

interface MessageListProps {
  conversation: Conversation
  isLoading: boolean
  errorMessage: string | null
  onRetry: () => void
  older: OlderMessages
}

export function MessageList({ conversation, isLoading, errorMessage, onRetry, older }: MessageListProps) {
  const { user } = useAuth()
  const now = useNow()
  const messages = useChatStore((state) => state.messages[conversation.id]) ?? NO_MESSAGES
  const pending = useChatStore((state) => state.pending[conversation.id]) ?? NO_PENDING

  const items = useMemo(
    () =>
      buildTimeline(messages, pending, {
        isGroup: conversation.type === 'Group',
        currentUserId: user?.userId ?? '',
        now,
      }),
    [messages, pending, conversation.type, user?.userId, now],
  )

  // Follows new messages only while the reader is at the bottom, so reading older ones is not
  // interrupted. A message the reader just sent always scrolls into view.
  const scroller = useRef<HTMLDivElement>(null)
  const atBottom = useRef(true)

  function handleScroll() {
    const element = scroller.current
    if (element) {
      atBottom.current = element.scrollHeight - element.scrollTop - element.clientHeight < BOTTOM_THRESHOLD
    }
  }

  // The first message and the content height as of the previous render. When the first message
  // changes, older messages were added above the reader; browsers would let the content jump down by
  // their height, so the scroll position is moved by the same amount. The scroller turns the
  // browser's own scroll anchoring off (overflow-anchor) so that this is the only adjustment.
  const previous = useRef<{ items: typeof items; firstId: string | undefined; scrollHeight: number } | null>(null)
  const firstId = messages[0]?.id

  useLayoutEffect(() => {
    const element = scroller.current
    if (!element) return

    const before = previous.current

    if (before && wasPrepended(before.firstId, firstId)) {
      element.scrollTop = scrollTopAfterPrepend(
        { scrollTop: element.scrollTop, scrollHeight: before.scrollHeight },
        element.scrollHeight,
      )
    } else if (before?.items !== items || before.scrollHeight !== element.scrollHeight) {
      // The height can change without the items changing (the row at the top that offers older
      // messages appears once the first page is known), which would otherwise leave a reader who
      // was at the bottom a little above it.
      const sentJustNow = items[items.length - 1]?.kind === 'pending'
      if (atBottom.current || sentJustNow) element.scrollTop = element.scrollHeight
    }

    previous.current = { items, firstId, scrollHeight: element.scrollHeight }
  })

  // Asks for older messages when the top of the list comes into view. A new observer reports where
  // the element is as soon as it starts watching, which is also what continues loading when a page
  // was too short to push the top out of view.
  const topOfList = useRef<HTMLDivElement>(null)
  const { hasMore, isLoading: loadingOlder, error: olderError, onLoad: loadOlder } = older

  useEffect(() => {
    const target = topOfList.current
    if (!target || !hasMore || loadingOlder || olderError) return

    const observer = new IntersectionObserver(
      (entries) => {
        if (entries.some((entry) => entry.isIntersecting)) loadOlder()
      },
      { root: scroller.current, rootMargin: `${PRELOAD_MARGIN} 0px 0px 0px` },
    )
    observer.observe(target)

    return () => observer.disconnect()
  }, [hasMore, loadingOlder, olderError, loadOlder])

  const nothingToShow = items.length === 0
  const reachedStart = !nothingToShow && !isLoading && !errorMessage && !hasMore && !loadingOlder && !olderError

  return (
    <div ref={scroller} onScroll={handleScroll} className="flex-1 overflow-y-auto px-4 py-4 [overflow-anchor:none]">
      {!nothingToShow && loadingOlder && (
        <div className="flex h-8 items-center justify-center gap-2 text-xs text-slate-500">
          <LoaderCircle className="h-3.5 w-3.5 animate-spin" />
          Loading earlier messages…
        </div>
      )}

      {!nothingToShow && !loadingOlder && olderError && (
        <div className="mb-2 flex flex-col items-center gap-2 text-center">
          <div className="flex items-start gap-2 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-600">
            <AlertCircle className="mt-0.5 h-4 w-4 shrink-0" />
            <span>{olderError}</span>
          </div>
          <Button variant="ghost" size="sm" onClick={loadOlder}>
            Try again
          </Button>
        </div>
      )}

      {!nothingToShow && !loadingOlder && !olderError && hasMore && <div ref={topOfList} className="h-8" />}

      {reachedStart && <p className="flex h-8 items-center justify-center text-xs text-slate-400">Beginning of the conversation</p>}

      {nothingToShow && isLoading && (
        <div className="flex h-full items-center justify-center gap-2 text-sm text-slate-500">
          <LoaderCircle className="h-4 w-4 animate-spin" />
          Loading messages…
        </div>
      )}

      {nothingToShow && !isLoading && errorMessage && (
        <div className="mx-auto mt-8 flex max-w-sm flex-col items-center gap-3 text-center">
          <div className="flex items-start gap-2 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-600">
            <AlertCircle className="mt-0.5 h-4 w-4 shrink-0" />
            <span>{errorMessage}</span>
          </div>
          <Button variant="ghost" size="sm" onClick={onRetry}>
            Try again
          </Button>
        </div>
      )}

      {nothingToShow && !isLoading && !errorMessage && (
        <p className="mt-8 text-center text-sm text-slate-500">No messages yet. Say hello.</p>
      )}

      {items.map((item) => {
        if (item.kind === 'day') {
          return (
            <div key={item.key} className="my-4 flex justify-center">
              <span className="rounded-full bg-slate-200/70 px-3 py-1 text-xs font-medium text-slate-500">
                {item.label}
              </span>
            </div>
          )
        }

        if (item.kind === 'pending') {
          return (
            <MessageBubble
              key={item.key}
              own
              continuing={item.continuing}
              content={item.pending.content}
              status={item.pending.status}
              error={item.pending.error}
              onRetry={() => void retryMessage(conversation.id, item.pending.clientMessageId)}
              onDismiss={() => dismissMessage(conversation.id, item.pending.clientMessageId)}
            />
          )
        }

        return (
          <MessageBubble
            key={item.key}
            own={item.own}
            continuing={item.continuing}
            senderName={item.showSender ? item.message.senderDisplayName : undefined}
            content={item.message.content}
            createdAt={item.message.createdAt}
          />
        )
      })}
    </div>
  )
}
