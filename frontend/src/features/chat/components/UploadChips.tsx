import { AlertCircle, X } from 'lucide-react'
import { Button } from '../../../components/ui/Button'
import { cn } from '../../../lib/cn'
import { formatFileSize } from '../../../lib/format'
import { attachmentTypeOf } from '../attachments'
import type { UploadItem } from '../uploads'
import { AttachmentIcon } from './AttachmentIcon'
import { PreviewImage } from './PreviewImage'

interface UploadChipsProps {
  items: UploadItem[]
  onRemove: (id: string) => void
}

// The files waiting to be sent with the message being written, each with how far its upload has got. Removing
// one of them cancels its upload if it is still going.
export function UploadChips({ items, onRemove }: UploadChipsProps) {
  return (
    <ul className="mb-2 flex flex-wrap gap-2">
      {items.map((item) => {
        const failed = item.status === 'failed'

        return (
          <li
            key={item.id}
            className={cn(
              'flex w-56 max-w-full items-center gap-2 rounded-lg border bg-white p-1.5 text-xs',
              failed ? 'border-red-400' : 'border-line',
            )}
          >
            {attachmentTypeOf(item.file.type) === 'Image' ? (
              <PreviewImage file={item.file} alt="" className="h-10 w-10 shrink-0 rounded-lg object-cover" />
            ) : (
              <span className="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-slate-100 text-slate-500">
                <AttachmentIcon contentType={item.file.type} className="h-5 w-5" />
              </span>
            )}

            <span className="min-w-0 flex-1">
              <span className="block truncate font-medium text-slate-900">{item.file.name}</span>

              {item.status === 'uploading' && (
                <span
                  role="progressbar"
                  aria-label={`Uploading ${item.file.name}`}
                  aria-valuemin={0}
                  aria-valuemax={100}
                  aria-valuenow={Math.round(item.progress * 100)}
                  className="mt-1 block h-1 overflow-hidden rounded-full bg-slate-200"
                >
                  <span className="block h-full bg-blue-600" style={{ width: `${Math.round(item.progress * 100)}%` }} />
                </span>
              )}

              {item.status === 'ready' && <span className="text-slate-500">{formatFileSize(item.file.size)}</span>}

              {failed && (
                <span className="flex items-start gap-1 text-red-500" role="alert">
                  <AlertCircle className="mt-0.5 h-3 w-3 shrink-0" aria-hidden="true" />
                  <span>{item.error}</span>
                </span>
              )}
            </span>

            <Button
              variant="ghost"
              size="icon"
              className="h-7 w-7"
              aria-label={`Remove ${item.file.name}`}
              onClick={() => onRemove(item.id)}
            >
              <X className="h-4 w-4" />
            </Button>
          </li>
        )
      })}
    </ul>
  )
}
