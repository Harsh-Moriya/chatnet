import { Download } from 'lucide-react'
import { useRef } from 'react'
import { cn } from '../../../lib/cn'
import { formatFileSize } from '../../../lib/format'
import { refreshAttachmentUrl } from '../api/chatApi'
import type { Attachment } from '../types'
import { AttachmentIcon } from './AttachmentIcon'

interface AttachmentGridProps {
  attachments: Attachment[]
  own: boolean
  conversationId: string
  messageId: string
}

// The files of a confirmed message: pictures and videos drawn in the message, anything else as a chip that
// downloads. Nothing is fetched until it is near the screen (the pictures load lazily).
export function AttachmentGrid({ attachments, own, conversationId, messageId }: AttachmentGridProps) {
  // The attachments whose link has already been renewed once from this message. A link that is still dead
  // after that is not asked for again, so a file that is really gone does not cause a request per render.
  const renewed = useRef(new Set<string>())

  function linkFailed(attachment: Attachment) {
    if (renewed.current.has(attachment.id)) return
    renewed.current.add(attachment.id)

    // If the renewal fails too, the picture stays broken and shows its alternative text; there is nothing
    // more to do from here, and the next history load brings fresh links anyway.
    refreshAttachmentUrl(conversationId, messageId, attachment.id).catch(() => undefined)
  }

  const shown = attachments.filter((a) => a.type !== 'File')
  const files = attachments.filter((a) => a.type === 'File')

  return (
    <div className="flex flex-col gap-1.5">
      {shown.length > 0 && (
        <div className={cn('grid w-64 max-w-full gap-1', shown.length === 1 ? 'grid-cols-1' : 'grid-cols-2')}>
          {shown.map((attachment) =>
            attachment.type === 'Image' ? (
              <a
                key={attachment.id}
                href={attachment.url}
                target="_blank"
                rel="noopener noreferrer"
                className="block overflow-hidden rounded-lg bg-slate-100"
              >
                <img
                  src={attachment.url}
                  alt={attachment.fileName}
                  loading="lazy"
                  decoding="async"
                  onError={() => linkFailed(attachment)}
                  className={cn('w-full object-cover', shown.length === 1 ? 'max-h-72 min-h-16' : 'aspect-square')}
                />
              </a>
            ) : (
              <video
                key={attachment.id}
                src={attachment.url}
                aria-label={attachment.fileName}
                controls
                preload="metadata"
                onError={() => linkFailed(attachment)}
                className={cn('w-full rounded-lg bg-black', shown.length === 1 ? 'max-h-72' : 'aspect-square')}
              />
            ),
          )}
        </div>
      )}

      {files.map((attachment) => (
        <a
          key={attachment.id}
          href={attachment.url}
          target="_blank"
          rel="noopener noreferrer"
          className={cn(
            'flex w-64 max-w-full items-center gap-2 rounded-lg border px-2.5 py-2 text-xs transition-colors',
            own
              ? 'border-white/30 bg-white/15 text-white hover:bg-white/25'
              : 'border-line bg-slate-50 text-slate-900 hover:bg-slate-100',
          )}
        >
          <AttachmentIcon contentType={attachment.contentType} className="h-5 w-5 shrink-0" />
          <span className="min-w-0 flex-1">
            <span className="block truncate font-medium">{attachment.fileName}</span>
            <span className={own ? 'text-blue-100' : 'text-slate-500'}>{formatFileSize(attachment.sizeBytes)}</span>
          </span>
          <Download className="h-4 w-4 shrink-0" aria-label="Download" />
        </a>
      ))}
    </div>
  )
}
