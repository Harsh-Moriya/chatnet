import { BellOff, Paperclip, Phone, Video } from 'lucide-react'
import { NavLink } from 'react-router-dom'
import { Avatar } from '../../../components/ui/Avatar'
import { cn } from '../../../lib/cn'
import { formatListTime } from '../../../lib/format'
import { usePresenceStore } from '../../presence/store/presenceStore'
import { useChatStore } from '../store/chatStore'
import { attachmentPreview } from '../attachments'
import { callPreview } from '../calls'
import { unreadLabel } from '../readState'
import { typingLabel } from '../typingLabel'
import type { Conversation } from '../types'
import { unreadableText } from '../unreadable'

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
    <li className="px-2">
      <NavLink
        to={`/chat/${conversation.id}`}
        className={({ isActive }) =>
          cn(
            'flex items-center gap-3 rounded-2xl px-3 py-2.5 transition-colors hover:bg-hover',
            isActive && 'bg-accent-soft hover:bg-accent-soft',
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
              size="xl"
            />

            <div className="min-w-0 flex-1">
              <div className="flex items-baseline justify-between gap-2">
                <p className={cn('flex min-w-0 items-center gap-1.5 text-body text-fg', unread ? 'font-bold' : 'font-semibold')}>
                  <span className="truncate">{conversation.name}</span>
                  {conversation.isMuted && (
                    <BellOff className="h-3.5 w-3.5 shrink-0 text-fg-faint" role="img" aria-label="Muted" />
                  )}
                  {conversation.leftAt !== null && (
                    <span className="shrink-0 rounded-full bg-subtle px-1.5 py-0.5 text-[10px] font-medium text-fg-muted">
                      Left
                    </span>
                  )}
                </p>
                <time className={cn('shrink-0 text-xs', unread ? 'font-medium text-accent-fg' : 'text-fg-muted')} dateTime={last?.createdAt ?? conversation.updatedAt}>
                  {formatListTime(last?.createdAt ?? conversation.updatedAt, now)}
                </time>
              </div>

              <div className="flex items-center justify-between gap-2">
                <p className={cn('truncate text-[13px] leading-snug', unread ? 'font-medium text-fg' : 'text-fg-muted')}>
                  {typing ? (
                    <span className="text-accent-fg">{typing}</span>
                  ) : last === null ? (
                    <span className="italic">No messages yet</span>
                  ) : last.kind === 'Call' && last.call ? (
                    <>
                      {last.call.media === 'Video' ? (
                        <Video className="mr-1 inline h-3.5 w-3.5 align-text-bottom" aria-hidden="true" />
                      ) : (
                        <Phone className="mr-1 inline h-3.5 w-3.5 align-text-bottom" aria-hidden="true" />
                      )}
                      {callPreview(last.call, last.senderId === currentUserId)}
                    </>
                  ) : last.unreadable ? (
                    <span className="italic">{unreadableText(last.unreadable)}</span>
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
                      'inline-flex h-5 min-w-5 shrink-0 items-center justify-center rounded-full px-1.5 text-[11px] font-semibold text-white',
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
