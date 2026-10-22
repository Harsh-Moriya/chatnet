import { AlertCircle, CheckCheck, Clock, RotateCw } from 'lucide-react'
import type { ReactNode } from 'react'
import { Button } from '../../../components/ui/Button'
import { cn } from '../../../lib/cn'
import { formatClock } from '../../../lib/format'
import type { Seen } from '../readState'

interface MessageBubbleProps {
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
  // Set on the reader's newest message once someone has read it.
  seen?: Seen
  status?: 'sending' | 'failed'
  error?: string | null
  onRetry?: () => void
  onDismiss?: () => void
}

export function MessageBubble({
  own,
  continuing,
  senderName,
  content,
  attachments,
  createdAt,
  seen,
  status,
  error,
  onRetry,
  onDismiss,
}: MessageBubbleProps) {
  const failed = status === 'failed'
  const deleted = content === null

  return (
    <div className={cn('flex flex-col', own ? 'items-end' : 'items-start', continuing ? 'mt-0.5' : 'mt-3')}>
      {senderName && <p className="mb-1 px-1 text-xs font-medium text-slate-500">{senderName}</p>}

      <div
        className={cn(
          'max-w-[85%] rounded-lg px-3 py-2 text-sm sm:max-w-[75%]',
          // One tone only. Two background or text colours in the same class list would be decided
          // by their order in the stylesheet, and a failed message once ended up white on pale red.
          failed
            ? 'border border-red-400 bg-red-50 text-slate-900'
            : own
              ? 'bg-blue-600 text-white'
              : 'border border-slate-200 bg-white text-slate-900',
          status === 'sending' && 'opacity-70',
        )}
      >
        {deleted ? (
          <p className={cn('italic', own ? 'text-blue-100' : 'text-slate-400')}>This message was deleted</p>
        ) : (
          <>
            {attachments}
            {content !== '' && (
              <p className={cn('whitespace-pre-wrap break-words', attachments ? 'mt-2' : undefined)}>{content}</p>
            )}
          </>
        )}

        <p
          className={cn(
            'mt-1 flex items-center justify-end gap-1 text-[11px] leading-none',
            own && !failed ? 'text-blue-100' : 'text-slate-400',
          )}
        >
          {status === 'sending' ? (
            <>
              <Clock className="h-3 w-3" aria-hidden="true" />
              <span>Sending</span>
            </>
          ) : (
            createdAt && <time dateTime={createdAt}>{formatClock(createdAt)}</time>
          )}
        </p>
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
