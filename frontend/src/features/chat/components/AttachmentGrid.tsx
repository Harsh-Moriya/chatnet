import { AlertCircle, Download, Lock, Maximize2, Play } from 'lucide-react'
import { type ReactNode, useState } from 'react'
import { Button } from '../../../components/ui/Button'
import { cn } from '../../../lib/cn'
import { formatFileSize } from '../../../lib/format'
import type { FileRef } from '../../../lib/crypto/message'
import { cleanFileName, isInlinePicture, kindOf } from '../files/fileNames'
import type { FileSource } from '../files/fileObjects'
import { saveFile } from '../files/openFiles'
import { useFileObject } from '../hooks/useFileObject'
import { useInView } from '../hooks/useInView'
import type { Attachment } from '../types'
import { useViewerStore } from '../viewer/viewerStore'
import { AttachmentIcon } from './AttachmentIcon'

interface AttachmentGridProps {
  attachments: Attachment[]
  own: boolean
  conversationId: string
  messageId: string
}

// An attachment the sealed message vouches for, with what is needed to find and open it.
type Opened = { attachment: Attachment; file: FileRef; source: FileSource }

// The files of a confirmed message. The files are encrypted in the bucket, so each is fetched and opened here: pictures as they come near the
// screen when they are not large, and anything else when it is asked for (a video is then played in place, any other file is saved, and a
// large picture is opened in the viewer, as a picture is when it is clicked). A file the sealed message does not vouch for is not drawn, and
// says that it cannot be opened.
export function AttachmentGrid({ attachments, own, conversationId, messageId }: AttachmentGridProps) {
  const openViewer = useViewerStore((state) => state.open)
  const vouched: Opened[] = []
  const unopenable: Attachment[] = []

  for (const attachment of attachments) {
    if (attachment.file === null) unopenable.push(attachment)
    else vouched.push({ attachment, file: attachment.file, source: { id: attachment.id, conversationId, messageId, url: attachment.url, entry: attachment.file } })
  }

  const pictures = vouched.filter((item) => isInlinePicture(item.file))
  const videos = vouched.filter((item) => kindOf(item.file) === 'Video')
  const files = vouched.filter((item) => !isInlinePicture(item.file) && kindOf(item.file) !== 'Video')

  return (
    <div className="flex flex-col gap-1.5">
      {pictures.length > 0 && (
        <div className={cn('grid w-64 max-w-full gap-1', pictures.length === 1 ? 'grid-cols-1' : 'grid-cols-2')}>
          {pictures.map((item) => (
            <PictureTile key={item.attachment.id} item={item} alone={pictures.length === 1} onOpen={() => openViewer(conversationId, item.attachment.id)} />
          ))}
        </div>
      )}

      {videos.map((item) => (
        <VideoTile key={item.attachment.id} item={item} own={own} />
      ))}

      {files.map((item) => (
        <FileChip
          key={item.attachment.id}
          item={item}
          own={own}
          onOpen={kindOf(item.file) === 'Image' ? () => openViewer(conversationId, item.attachment.id) : undefined}
        />
      ))}

      {unopenable.map((attachment) => (
        <div
          key={attachment.id}
          className={cn(
            'flex w-64 max-w-full items-center gap-2 rounded-lg border px-2.5 py-2 text-xs italic',
            own ? 'border-white/30 bg-white/15 text-blue-100' : 'border-line bg-slate-50 text-slate-500',
          )}
        >
          <Lock className="h-4 w-4 shrink-0" aria-hidden="true" />
          <span>This file can&apos;t be opened</span>
        </div>
      ))}
    </div>
  )
}

function PictureTile({ item, alone, onOpen }: { item: Opened; alone: boolean; onOpen: () => void }) {
  const [attach, near] = useInView<HTMLDivElement>()
  const [state, retry] = useFileObject(item.source, near)
  const name = cleanFileName(item.file.name)

  return (
    <div ref={attach} className="overflow-hidden rounded-lg bg-slate-100">
      {state.status === 'ready' ? (
        <Button variant="ghost" aria-label={`Open ${name}`} className="h-auto w-full rounded-none p-0 hover:bg-transparent active:bg-transparent" onClick={onOpen}>
          <img src={state.url} alt={name} decoding="async" className={cn('w-full object-cover', alone ? 'max-h-72 min-h-16' : 'aspect-square')} />
        </Button>
      ) : state.status === 'failed' ? (
        <Button variant="ghost" className={cn('h-auto w-full flex-col py-4 text-xs text-slate-500', alone ? 'min-h-24' : 'aspect-square')} onClick={retry}>
          <AlertCircle className="h-4 w-4" aria-hidden="true" />
          <span>Couldn&apos;t load {name}. Tap to try again.</span>
        </Button>
      ) : (
        <div role="img" aria-label={`Loading ${name}`} className={cn('animate-pulse', alone ? 'h-40' : 'aspect-square')} />
      )}
    </div>
  )
}

function VideoTile({ item, own }: { item: Opened; own: boolean }) {
  const [asked, setAsked] = useState(false)
  const [state, retry] = useFileObject(item.source, asked)
  const name = cleanFileName(item.file.name)

  if (state.status === 'ready') {
    return <video src={state.url} aria-label={name} controls autoPlay className="max-h-72 w-64 max-w-full rounded-lg bg-black" />
  }

  const failed = state.status === 'failed'
  return (
    <ChipButton
      own={own}
      icon={failed ? <AlertCircle className="h-5 w-5 shrink-0" aria-hidden="true" /> : <Play className="h-5 w-5 shrink-0" aria-hidden="true" />}
      name={name}
      detail={failed ? "Couldn't load it. Tap to try again." : state.status === 'loading' ? 'Opening...' : formatFileSize(item.file.size)}
      busy={state.status === 'loading'}
      onClick={failed ? retry : () => setAsked(true)}
      label={`Play ${name}`}
    />
  )
}

// A file to save, or a picture too large to be drawn in the message, which opens in the viewer instead.
function FileChip({ item, own, onOpen }: { item: Opened; own: boolean; onOpen?: () => void }) {
  const [saving, setSaving] = useState(false)
  const [failed, setFailed] = useState(false)
  const name = cleanFileName(item.file.name)

  function save() {
    setSaving(true)
    setFailed(false)
    saveFile(item.source)
      .catch(() => setFailed(true))
      .finally(() => setSaving(false))
  }

  return (
    <ChipButton
      own={own}
      icon={<AttachmentIcon contentType={item.file.type} className="h-5 w-5 shrink-0" />}
      name={name}
      detail={failed ? "Couldn't open it. Tap to try again." : saving ? 'Opening...' : formatFileSize(item.file.size)}
      busy={saving}
      onClick={onOpen ?? save}
      label={onOpen ? `Open ${name}` : `Save ${name}`}
      trailing={onOpen ? <Maximize2 className="h-4 w-4 shrink-0" aria-hidden="true" /> : <Download className="h-4 w-4 shrink-0" aria-hidden="true" />}
    />
  )
}

interface ChipButtonProps {
  own: boolean
  icon: ReactNode
  name: string
  detail: string
  busy: boolean
  label: string
  trailing?: ReactNode
  onClick: () => void
}

function ChipButton({ own, icon, name, detail, busy, label, trailing, onClick }: ChipButtonProps) {
  return (
    <Button
      variant="ghost"
      aria-label={label}
      disabled={busy}
      onClick={onClick}
      className={cn(
        'h-auto w-64 max-w-full justify-start rounded-lg border px-2.5 py-2 text-left text-xs',
        own ? 'border-white/30 bg-white/15 text-white hover:bg-white/25 active:bg-white/30' : 'border-line bg-slate-50 text-slate-900 hover:bg-slate-100',
      )}
    >
      {icon}
      <span className="min-w-0 flex-1">
        <span className="block truncate font-medium">{name}</span>
        <span className={own ? 'text-blue-100' : 'text-slate-500'}>{detail}</span>
      </span>
      {trailing}
    </Button>
  )
}
