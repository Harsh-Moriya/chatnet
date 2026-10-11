import { Link, useParams } from 'react-router-dom'
import { AlertCircle, LoaderCircle } from 'lucide-react'
import { getApiError } from '../../../lib/errors'
import { useConversations } from '../api/useConversations'
import { useMessages } from '../api/useMessages'
import { useChatStore } from '../store/chatStore'
import type { Conversation } from '../types'
import { ChatHeader } from './ChatHeader'
import { MessageComposer } from './MessageComposer'
import { MessageList } from './MessageList'
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

  return (
    <>
      <ChatHeader conversation={conversation} />
      <MessageList
        conversation={conversation}
        isLoading={history.isPending}
        errorMessage={history.isError ? getApiError(history.error) : null}
        onRetry={() => void history.refetch()}
      />
      <TypingIndicator conversationId={conversation.id} />
      <MessageComposer conversationId={conversation.id} />
    </>
  )
}
