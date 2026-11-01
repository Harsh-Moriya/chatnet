import { BellOff, Paperclip } from 'lucide-react'
import { NavLink } from 'react-router-dom'
import { Avatar } from '../../../components/ui/Avatar'
import { cn } from '../../../lib/cn'
import { formatListTime } from '../../../lib/format'
import { usePresenceStore } from '../../presence/store/presenceStore'
import { useChatStore } from '../store/chatStore'
import { attachmentPreview } from '../attachments'
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

  // A direct chat shows whether the other person is online. A group has no single person to show, so
  // its dot is left out; the header says how many are online. The selector returns a boolean, so the
  // row re-renders only when this one person comes or goes.
  const online = usePresenceStore((state) =>
    conversation.otherUserId !== null && state.byUser[conversation.otherUserId]?.isOnline === true,
  )

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
            <Avatar
              name={conversation.name}
              src={conversation.avatarUrl}
              isGroup={conversation.type === 'Group'}
              online={online}
            />

            <div className="min-w-0 flex-1">
              <div className="flex items-baseline justify-between gap-2">
                <p className="flex min-w-0 items-center gap-1.5 text-sm font-semibold text-slate-900">
                  <span className="truncate">{conversation.name}</span>
                  {conversation.isMuted && (
                    <BellOff className="h-3.5 w-3.5 shrink-0 text-slate-400" role="img" aria-label="Muted" />
                  )}
                  {conversation.leftAt !== null && (
                    <span className="shrink-0 rounded-full bg-slate-100 px-1.5 py-0.5 text-[10px] font-medium text-slate-500">
                      Left
                    </span>
                  )}
                </p>
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
                      {last.attachmentCount > 0 && (
                        <Paperclip className="mr-1 inline h-3.5 w-3.5 align-text-bottom" aria-hidden="true" />
                      )}
                      {last.content === '' ? attachmentPreview(last.attachmentCount) : last.content}
                    </>
                  )}
                </p>

                {/* Not shown for the conversation that is open: its messages are in front of the reader,
                    and the jump button in the list says how many are below. */}
                {unread && !isActive && (
                  <span
                    className={cn(
                      'inline-flex h-5 min-w-5 shrink-0 items-center justify-center rounded-full px-1.5 text-xs font-semibold text-white',
                      // A muted conversation still counts what is unread, but does not ask for attention.
                      conversation.isMuted ? 'bg-slate-500' : 'bg-blue-600',
                    )}
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
