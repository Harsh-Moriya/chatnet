import { cn } from '../../../lib/cn'
import { formatFileSize } from '../../../lib/format'
import type { PendingAttachment } from '../types'
import { AttachmentIcon } from './AttachmentIcon'
import { PreviewImage } from './PreviewImage'

// The files of a message the server has not confirmed yet, drawn from what is in memory. The sender sees the
// pictures they chose at once; the files get their links when the confirmed message replaces this one.
export function PendingAttachments({ attachments }: { attachments: PendingAttachment[] }) {
  const pictures = attachments.filter((a) => a.type === 'Image')
  const others = attachments.filter((a) => a.type !== 'Image')

  return (
    <div className="flex flex-col gap-1.5">
      {pictures.length > 0 && (
        <div className={cn('grid w-64 max-w-full gap-1', pictures.length === 1 ? 'grid-cols-1' : 'grid-cols-2')}>
          {pictures.map((attachment) => (
            <div key={attachment.uploadKey} className="overflow-hidden rounded-lg bg-slate-100">
              <PreviewImage
                file={attachment.file}
                alt={attachment.fileName}
                className={cn('w-full object-cover', pictures.length === 1 ? 'max-h-72 min-h-16' : 'aspect-square')}
              />
            </div>
          ))}
        </div>
      )}

      {others.map((attachment) => (
        <div
          key={attachment.uploadKey}
          className="flex w-64 max-w-full items-center gap-2 rounded-lg border border-white/30 bg-white/15 px-2.5 py-2 text-xs text-white"
        >
          <AttachmentIcon contentType={attachment.contentType} className="h-5 w-5 shrink-0" />
          <span className="min-w-0 flex-1">
            <span className="block truncate font-medium">{attachment.fileName}</span>
            <span className="text-blue-100">{formatFileSize(attachment.sizeBytes)}</span>
          </span>
        </div>
      ))}
    </div>
  )
}
