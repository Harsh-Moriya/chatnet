import { useMemo } from 'react'
import { Button } from '../../../components/ui/Button'
import { getApiError } from '../../../lib/errors'
import { useNow } from '../../../lib/useNow'
import { useAuth } from '../../auth/hooks/useAuth'
import { useConversations } from '../api/useConversations'
import { filterConversations } from '../conversationFilter'
import { sortedConversations, useChatStore } from '../store/chatStore'
import { ConversationListItem } from './ConversationListItem'
import { Alert } from '../../../components/ui/Alert'
import { EmptyState } from '../../../components/ui/EmptyState'
import { RowsSkeleton } from '../../../components/ui/Skeleton'
import { MessagesSquare, SearchX } from 'lucide-react'

interface ConversationListProps {
  // What is typed in the box above the list: only the conversations with it in their name are shown.
  filter: string
  // Starts something new, from the list when it is empty or when nothing matches.
  onNewConversation: () => void
}

export function ConversationList({ filter, onNewConversation }: ConversationListProps) {
  const query = useConversations()
  const { user } = useAuth()
  const now = useNow()
  const conversations = useChatStore((state) => state.conversations)

  // Sorted here, not in a store selector: a selector that builds a new array on every call makes
  // React re-render without end.
  const list = useMemo(() => sortedConversations(conversations), [conversations])
  const shown = useMemo(() => filterConversations(list, filter), [list, filter])

  if (list.length === 0) {
    if (query.isPending) return <RowsSkeleton label="Loading conversations" />

    if (query.isError) {
      return (
        <div className="flex flex-col items-center gap-3 p-6 text-center">
          <Alert>{getApiError(query.error)}</Alert>
          <Button variant="ghost" size="sm" onClick={() => void query.refetch()}>
            Try again
          </Button>
        </div>
      )
    }

    return (
      <EmptyState
        icon={MessagesSquare}
        title="No conversations yet"
        action={
          <Button size="sm" onClick={onNewConversation}>
            Start a conversation
          </Button>
        }
      >
        Find someone by name to start chatting.
      </EmptyState>
    )
  }

  if (shown.length === 0) {
    return (
      <EmptyState
        icon={SearchX}
        title={`No conversation matches “${filter.trim()}”`}
        action={
          <Button variant="secondary" size="sm" onClick={onNewConversation}>
            Find someone new
          </Button>
        }
      />
    )
  }

  return (
    <nav aria-label="Conversations" className="flex-1 overflow-y-auto">
      <ul className="space-y-0.5 py-2">
        {shown.map((conversation) => (
          <ConversationListItem
            key={conversation.id}
            conversation={conversation}
            currentUserId={user?.userId ?? ''}
            now={now}
          />
        ))}
      </ul>
    </nav>
  )
}
