import { AlertCircle, CheckCheck, Clock, Lock, RotateCw } from 'lucide-react'
import type { ReactNode } from 'react'
import { Avatar } from '../../../components/ui/Avatar'
import { Button } from '../../../components/ui/Button'
import { Menu, type MenuItem } from '../../../components/ui/Menu'
import { cn } from '../../../lib/cn'
import { formatClock } from '../../../lib/format'
import { messageDomId } from '../messageDom'
import type { Seen } from '../readState'
import type { Member, Unreadable } from '../types'
import { unreadableText } from '../unreadable'
import { MessageText } from './MessageText'

interface MessageBubbleProps {
  // The confirmed message's id; absent while it is unconfirmed.
  messageId?: string
  // Drawn with a ring for a moment after a notification took the reader to it.
  highlighted?: boolean
  // Who is in the conversation and who is reading, to pick the mentions out of the text.
  members?: readonly Member[]
  selfUserName?: string
  own: boolean
  continuing: boolean
  // The last message of a run: its corner toward the sender is small, and it is the one that shows the time.
  endsRun: boolean
  // In a group, for a message of someone else: the sender's picture beside the first message of a run, and room for it beside the rest.
  leading?: 'indent' | { name: string; src: string | null }
  // Shown above the bubble; only set when the sender should be named.
  senderName?: string
  // Null for a deleted message and for one whose text cannot be shown (see `unreadable`). Empty for a message of only files.
  content: string | null
  // Why the text cannot be shown, when the message was not deleted.
  unreadable?: Unreadable | null
  // The message's files, drawn above its text.
  attachments?: ReactNode
  // Absent while the message is unconfirmed.
  createdAt?: string
  // When the sender last edited it; shown as "edited" next to the time. Null or absent for a message nobody edited.
  editedAt?: string | null
  // What the reader may do with the message (edit, delete). The menu is drawn only when there is something to offer.
  actions?: MenuItem[]
  // Takes the place of the text while the message is being edited.
  editor?: ReactNode
  // Set on the reader's newest message once someone has read it.
  seen?: Seen
  status?: 'sending' | 'failed'
  error?: string | null
  onRetry?: () => void
  onDismiss?: () => void
}

export function MessageBubble({
  messageId,
  highlighted = false,
  members,
  selfUserName,
  own,
  continuing,
  endsRun,
  leading,
  senderName,
  content,
  unreadable,
  attachments,
  createdAt,
  editedAt,
  actions,
  editor,
  seen,
  status,
  error,
  onRetry,
  onDismiss,
}: MessageBubbleProps) {
  const failed = status === 'failed'
  const deleted = content === null && !unreadable
  // While it is being edited the bubble is a plain white box, so the editor's buttons and text read on any message.
  const editing = editor !== undefined && !deleted
  // The colour of the bubble decides the colour of the ring drawn inside it.
  const onBlue = own && !failed && !editing
  // The time stays in the page for a screen reader on every message and is drawn on the last of a run, and wherever it carries news.
  const showFooter = endsRun || status === 'sending' || (editedAt != null && !deleted)

  return (
    <div
      id={messageId ? messageDomId(messageId) : undefined}
      className={cn('relative flex flex-col', own ? 'items-end' : 'items-start', continuing ? 'mt-0.5' : 'mt-4', leading && 'pl-9')}
    >
      {leading && leading !== 'indent' && (
        <div className="absolute left-0 top-0">
          <Avatar size="xs" name={leading.name} src={leading.src} />
        </div>
      )}

      {senderName && <p className="mb-1 px-1 text-xs font-medium text-fg-muted">{senderName}</p>}

      {/* The menu sits beside the bubble, on the side facing the middle of the page, and shows when the pointer is
          over the row or focus is inside it (always on a touch screen, which has no pointer to hover with). */}
      <div className={cn('group flex max-w-[85%] items-start gap-1 sm:max-w-[75%]', own && 'flex-row-reverse')}>
        <div
          className={cn(
            'min-w-0 rounded-bubble px-3.5 py-2 text-body transition-shadow',
            // The corner toward the sender is small on the last message of a run, which is what joins a run into one speaker.
            endsRun && (own ? 'rounded-br-bubble-tail' : 'rounded-bl-bubble-tail'),
            // Inside the bubble and not around it: messages of one sender sit 2 pixels apart, and a ring outside would be drawn
            // over the next message.
            highlighted && cn('ring-2 ring-inset', onBlue ? 'ring-sky-300' : 'ring-blue-500'),
            // One tone only. Two background or text colours in the same class list would be decided
            // by their order in the stylesheet, and a failed message once ended up white on pale red.
            failed
              ? 'border border-red-400 bg-danger-soft text-fg'
              : onBlue
                ? 'bg-blue-600 text-white'
                : 'bg-surface text-fg shadow-card',
            status === 'sending' && 'opacity-70',
          )}
        >
          {deleted ? (
            <p className={cn('italic', own ? 'text-blue-50' : 'text-fg-faint')}>This message was deleted</p>
          ) : unreadable ? (
            <p className={cn('flex items-center gap-1.5 italic', own ? 'text-blue-50' : 'text-fg-muted')}>
              <Lock className="h-3.5 w-3.5 shrink-0" aria-hidden="true" />
              {unreadableText(unreadable)}
            </p>
          ) : (
            <>
              {attachments}
              {editor ??
                (content !== null && content !== '' && (
                  <p className={cn('whitespace-pre-wrap break-words', attachments ? 'mt-2' : undefined)}>
                    <MessageText content={content} members={members} selfUserName={selfUserName} own={own} />
                  </p>
                ))}
            </>
          )}

          <p
            className={cn(
              'mt-1 flex items-center justify-end gap-1 text-[11px] leading-none',
              !showFooter && 'sr-only',
              own && !failed && !editing ? 'text-blue-50' : 'text-fg-faint',
            )}
          >
            {status === 'sending' ? (
              <>
                <Clock className="h-3 w-3" aria-hidden="true" />
                <span>Sending</span>
              </>
            ) : (
              <>
                {editedAt && !deleted && (
                  <span title={`Edited at ${formatClock(editedAt)}`}>edited</span>
                )}
                {createdAt && <time dateTime={createdAt}>{formatClock(createdAt)}</time>}
              </>
            )}
          </p>
        </div>

        {actions && actions.length > 0 && !editor && (
          <Menu
            label="Message actions"
            items={actions}
            align={own ? 'end' : 'start'}
            className="opacity-0 transition-opacity focus-within:opacity-100 group-hover:opacity-100 pointer-coarse:opacity-100"
          />
        )}
      </div>

      {seen && (
        <p className="mt-0.5 flex items-center gap-1 px-1 text-[11px] text-fg-faint" title={seen.title}>
          <CheckCheck className="h-3 w-3" aria-hidden="true" />
          {seen.label}
        </p>
      )}

      {failed && (
        <div className="mt-1 flex flex-wrap items-center justify-end gap-x-2 gap-y-1 text-xs text-danger-fg" role="alert">
          <AlertCircle className="h-3.5 w-3.5 shrink-0" aria-hidden="true" />
          <span>{error ?? 'Could not send.'}</span>
          <Button variant="ghost" size="sm" onClick={onRetry}>
            <RotateCw className="h-3.5 w-3.5" />
            Retry
          </Button>
          <Button variant="ghost" size="sm" onClick={onDismiss}>
            Dismiss
          </Button>
        </div>
      )}
    </div>
  )
}
