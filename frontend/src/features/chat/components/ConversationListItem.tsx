import { NavLink } from 'react-router-dom'
import { Avatar } from '../../../components/ui/Avatar'
import { cn } from '../../../lib/cn'
import { formatListTime } from '../../../lib/format'
import { useChatStore } from '../store/chatStore'
import { unreadLabel } from '../readState'
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
  const unread = conversation.unreadCount > 0

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
        {({ isActive }) => (
          <>
            <Avatar name={conversation.name} src={conversation.avatarUrl} isGroup={conversation.type === 'Group'} />

            <div className="min-w-0 flex-1">
              <div className="flex items-baseline justify-between gap-2">
                <p className="truncate text-sm font-semibold text-slate-900">{conversation.name}</p>
                <time className="shrink-0 text-xs text-slate-500" dateTime={last?.createdAt ?? conversation.updatedAt}>
                  {formatListTime(last?.createdAt ?? conversation.updatedAt, now)}
                </time>
              </div>

              <div className="flex items-center justify-between gap-2">
                <p className={cn('truncate text-sm', unread ? 'font-medium text-slate-900' : 'text-slate-500')}>
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

                {/* Not shown for the conversation that is open: its messages are in front of the reader,
                    and the jump button in the list says how many are below. */}
                {unread && !isActive && (
                  <span
                    className="inline-flex h-5 min-w-5 shrink-0 items-center justify-center rounded-full bg-blue-600 px-1.5 text-xs font-semibold text-white"
                    aria-label={`${unreadLabel(conversation.unreadCount)} unread`}
                  >
                    {unreadLabel(conversation.unreadCount)}
                  </span>
                )}
              </div>
            </div>
          </>
        )}
      </NavLink>
    </li>
  )
}
