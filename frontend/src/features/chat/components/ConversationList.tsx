import { useMemo } from 'react'
import { AlertCircle } from 'lucide-react'
import { Button } from '../../../components/ui/Button'
import { getApiError } from '../../../lib/errors'
import { useNow } from '../../../lib/useNow'
import { useAuth } from '../../auth/hooks/useAuth'
import { useConversations } from '../api/useConversations'
import { filterConversations } from '../conversationFilter'
import { sortedConversations, useChatStore } from '../store/chatStore'
import { ConversationListItem } from './ConversationListItem'

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
    if (query.isPending) return <ListSkeleton />

    if (query.isError) {
      return (
        <div className="flex flex-col items-center gap-3 p-6 text-center">
          <div className="flex items-start gap-2 rounded-lg border border-danger-line bg-danger-soft px-3 py-2 text-sm text-danger-fg">
            <AlertCircle className="mt-0.5 h-4 w-4 shrink-0" />
            <span>{getApiError(query.error)}</span>
          </div>
          <Button variant="ghost" size="sm" onClick={() => void query.refetch()}>
            Try again
          </Button>
        </div>
      )
    }

    return (
      <div className="flex flex-col items-center gap-3 p-8 text-center">
        <p className="text-sm font-medium text-fg">No conversations yet</p>
        <p className="text-sm text-fg-muted">Find someone by name to start chatting.</p>
        <Button size="sm" onClick={onNewConversation}>
          Start a conversation
        </Button>
      </div>
    )
  }

  if (shown.length === 0) {
    return (
      <div className="flex flex-col items-center gap-3 p-8 text-center">
        <p className="text-sm text-fg-muted">No conversation matches &ldquo;{filter.trim()}&rdquo;.</p>
        <Button variant="secondary" size="sm" onClick={onNewConversation}>
          Find someone new
        </Button>
      </div>
    )
  }

  return (
    <nav aria-label="Conversations" className="flex-1 overflow-y-auto">
      <ul className="divide-y divide-line-subtle">
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

function ListSkeleton() {
  return (
    <div className="space-y-1 p-4" aria-hidden="true">
      {Array.from({ length: 6 }, (_, index) => (
        <div key={index} className="flex animate-pulse items-center gap-3 py-2">
          <div className="h-10 w-10 shrink-0 rounded-full bg-strong" />
          <div className="flex-1 space-y-2">
            <div className="h-3 w-1/2 rounded bg-strong" />
            <div className="h-3 w-3/4 rounded bg-subtle" />
          </div>
        </div>
      ))}
    </div>
  )
}
