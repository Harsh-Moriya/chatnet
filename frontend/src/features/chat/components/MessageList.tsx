import { useLayoutEffect, useMemo, useRef } from 'react'
import { AlertCircle, LoaderCircle } from 'lucide-react'
import { Button } from '../../../components/ui/Button'
import { useNow } from '../../../lib/useNow'
import { useAuth } from '../../auth/hooks/useAuth'
import { dismissMessage, retryMessage } from '../api/chatApi'
import { useChatStore } from '../store/chatStore'
import { buildTimeline } from '../timeline'
import type { Conversation, Message, PendingMessage } from '../types'
import { MessageBubble } from './MessageBubble'

// Stable empty values, so a conversation with nothing in it does not look like a change on every render.
const NO_MESSAGES: Message[] = []
const NO_PENDING: PendingMessage[] = []

// How close to the bottom still counts as "at the bottom", in pixels.
const BOTTOM_THRESHOLD = 96

interface MessageListProps {
  conversation: Conversation
  isLoading: boolean
  errorMessage: string | null
  onRetry: () => void
}

export function MessageList({ conversation, isLoading, errorMessage, onRetry }: MessageListProps) {
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

  useLayoutEffect(() => {
    const element = scroller.current
    if (!element) return

    const sentJustNow = items[items.length - 1]?.kind === 'pending'
    if (atBottom.current || sentJustNow) element.scrollTop = element.scrollHeight
  }, [items])

  const nothingToShow = items.length === 0

  return (
    <div ref={scroller} onScroll={handleScroll} className="flex-1 overflow-y-auto px-4 py-4">
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
