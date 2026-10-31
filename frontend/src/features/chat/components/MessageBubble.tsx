import { AlertCircle, CheckCheck, Clock, RotateCw } from 'lucide-react'
import type { ReactNode } from 'react'
import { Button } from '../../../components/ui/Button'
import { Menu, type MenuItem } from '../../../components/ui/Menu'
import { cn } from '../../../lib/cn'
import { formatClock } from '../../../lib/format'
import { messageDomId } from '../messageDom'
import type { Seen } from '../readState'
import type { Member } from '../types'
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
  // Shown above the bubble; only set when the sender should be named.
  senderName?: string
  // Null for a deleted message. Empty for a message of only files.
  content: string | null
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
  senderName,
  content,
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
  const deleted = content === null
  // While it is being edited the bubble is a plain white box, so the editor's buttons and text read on any message.
  const editing = editor !== undefined && !deleted

  return (
    <div
      id={messageId ? messageDomId(messageId) : undefined}
      className={cn('flex flex-col', own ? 'items-end' : 'items-start', continuing ? 'mt-0.5' : 'mt-3')}
    >
      {senderName && <p className="mb-1 px-1 text-xs font-medium text-slate-500">{senderName}</p>}

      {/* The menu sits beside the bubble, on the side facing the middle of the page, and shows when the pointer is
          over the row or focus is inside it (always on a touch screen, which has no pointer to hover with). */}
      <div className={cn('group flex max-w-[85%] items-start gap-1 sm:max-w-[75%]', own && 'flex-row-reverse')}>
        <div
          className={cn(
            'min-w-0 rounded-lg px-3 py-2 text-sm transition-shadow',
            highlighted && 'ring-2 ring-blue-500 ring-offset-2',
            // One tone only. Two background or text colours in the same class list would be decided
            // by their order in the stylesheet, and a failed message once ended up white on pale red.
            failed
              ? 'border border-red-400 bg-red-50 text-slate-900'
              : own && !editing
                ? 'bg-blue-600 text-white'
                : 'border border-line bg-white text-slate-900',
            status === 'sending' && 'opacity-70',
          )}
        >
          {deleted ? (
            <p className={cn('italic', own ? 'text-blue-100' : 'text-slate-400')}>This message was deleted</p>
          ) : (
            <>
              {attachments}
              {editor ??
                (content !== '' && (
                  <p className={cn('whitespace-pre-wrap break-words', attachments ? 'mt-2' : undefined)}>
                    <MessageText content={content} members={members} selfUserName={selfUserName} own={own} />
                  </p>
                ))}
            </>
          )}

          <p
            className={cn(
              'mt-1 flex items-center justify-end gap-1 text-[11px] leading-none',
              own && !failed && !editing ? 'text-blue-100' : 'text-slate-400',
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
        <p className="mt-0.5 flex items-center gap-1 px-1 text-[11px] text-slate-400" title={seen.title}>
          <CheckCheck className="h-3 w-3" aria-hidden="true" />
          {seen.label}
        </p>
      )}

      {failed && (
        <div className="mt-1 flex flex-wrap items-center justify-end gap-x-2 gap-y-1 text-xs text-red-500" role="alert">
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
