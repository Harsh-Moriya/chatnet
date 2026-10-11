import { NavLink } from 'react-router-dom'
import { Avatar } from '../../../components/ui/Avatar'
import { cn } from '../../../lib/cn'
import { formatListTime } from '../../../lib/format'
import { useChatStore } from '../store/chatStore'
import { typingLabel } from '../typingLabel'
import type { Conversation } from '../types'

interface ConversationListItemProps {
  conversation: Conversation
  currentUserId: string
  now: Date
}

export function ConversationListItem({ conversation, currentUserId, now }: ConversationListItemProps) {
  const typing = typingLabel(useChatStore((state) => state.typing[conversation.id]))
  const last = conversation.lastMessage

  return (
    <li>
      <NavLink
        to={`/chat/${conversation.id}`}
        className={({ isActive }) =>
          cn(
            'flex items-center gap-3 px-4 py-3 transition-colors hover:bg-slate-50',
            isActive && 'bg-blue-50 hover:bg-blue-50',
          )
        }
      >
        <Avatar name={conversation.name} src={conversation.avatarUrl} isGroup={conversation.type === 'Group'} />

        <div className="min-w-0 flex-1">
          <div className="flex items-baseline justify-between gap-2">
            <p className="truncate text-sm font-semibold text-slate-900">{conversation.name}</p>
            <time className="shrink-0 text-xs text-slate-500" dateTime={last?.createdAt ?? conversation.updatedAt}>
              {formatListTime(last?.createdAt ?? conversation.updatedAt, now)}
            </time>
          </div>

          <p className="truncate text-sm text-slate-500">
            {typing ? (
              <span className="text-blue-600">{typing}</span>
            ) : last === null ? (
              <span className="italic">No messages yet</span>
            ) : last.content === null ? (
              <span className="italic">Message deleted</span>
            ) : (
              <>
                {last.senderId === currentUserId && <span>You: </span>}
                {last.content}
              </>
            )}
          </p>
        </div>
      </NavLink>
    </li>
  )
}
