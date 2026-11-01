import { useCallback } from 'react'
import { Link, useParams } from 'react-router-dom'
import { AlertCircle, LoaderCircle } from 'lucide-react'
import { getApiError } from '../../../lib/errors'
import { formatClock, formatDayLabel } from '../../../lib/format'
import { useConversations } from '../api/useConversations'
import { useMembers } from '../api/useMembers'
import { useMessages } from '../api/useMessages'
import { useChatStore } from '../store/chatStore'
import type { Conversation } from '../types'
import { ChatHeader } from './ChatHeader'
import { MessageComposer } from './MessageComposer'
import { MessageList, type OlderMessages } from './MessageList'
import { TypingIndicator } from './TypingIndicator'

// The route element for /chat/:conversationId. It finds the conversation in the store (which the
// sidebar's request fills) and decides what to show while that is unknown.
export function ConversationView() {
  const { conversationId = '' } = useParams()
  const conversation = useChatStore((state) => state.conversations[conversationId])
  const list = useConversations()

  if (conversation) {
    // Keyed by id so switching conversation starts with a fresh scroll position and an empty box.
    return <ConversationPane key={conversation.id} conversation={conversation} />
  }

  if (list.isPending) {
    return (
      <div className="flex flex-1 items-center justify-center gap-2 text-sm text-slate-500">
        <LoaderCircle className="h-4 w-4 animate-spin" />
        Loading…
      </div>
    )
  }

  return (
    <div className="flex flex-1 flex-col items-center justify-center gap-3 p-8 text-center">
      <AlertCircle className="h-8 w-8 text-slate-400" />
      <h2 className="text-base font-semibold text-slate-900">
        {list.isError ? 'Could not load your conversations' : 'Conversation not found'}
      </h2>
      <p className="max-w-xs text-sm text-slate-500">
        {list.isError
          ? getApiError(list.error)
          : 'It may not exist, or you may not be a member of it.'}
      </p>
      <Link to="/chat" className="text-sm font-medium text-blue-600 hover:text-blue-700">
        Back to conversations
      </Link>
    </div>
  )
}

function ConversationPane({ conversation }: { conversation: Conversation }) {
  const history = useMessages(conversation.id)
  // A group the reader left stays readable, up to the moment they left, and can no longer be written to.
  const left = conversation.leftAt !== null
  // Loaded for who has read what; the members land in the store, which the list reads. A failure only
  // means no "seen" marks, so it shows nothing. Someone who left is not shown who is in the group.
  useMembers(conversation.id, !left)
  const { fetchNextPage } = history
  const loadOlder = useCallback(() => void fetchNextPage(), [fetchNextPage])

  // A failure to load an older page is shown at the top of the list, next to its "Try again", and
  // is not the failure of the conversation as a whole.
  const olderFailed = history.isFetchNextPageError
  const older: OlderMessages = {
    hasMore: history.hasNextPage,
    isLoading: history.isFetchingNextPage,
    error: olderFailed ? getApiError(history.error) : null,
    onLoad: loadOlder,
  }

  return (
    <>
      <ChatHeader conversation={conversation} />
      <MessageList
        conversation={conversation}
        isLoading={history.isPending}
        errorMessage={history.isError && !olderFailed ? getApiError(history.error) : null}
        onRetry={() => void history.refetch()}
        older={older}
      />
      {conversation.leftAt !== null ? (
        <LeftNotice leftAt={conversation.leftAt} />
      ) : (
        <>
          <TypingIndicator conversationId={conversation.id} />
          <MessageComposer conversationId={conversation.id} />
        </>
      )}
    </>
  )
}

// Where the composer would be, for a group the reader is no longer in.
function LeftNotice({ leftAt }: { leftAt: string }) {
  return (
    <p className="shrink-0 border-t border-line bg-white px-4 py-4 text-center text-sm text-slate-500">
      You are no longer a member of this group. You can read what was said until {formatDayLabel(leftAt, new Date())} at{' '}
      {formatClock(leftAt)}.
    </p>
  )
}
