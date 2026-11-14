import { ChevronLeft, ChevronRight, Download, LoaderCircle, X } from 'lucide-react'
import { Suspense, lazy, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Dialog } from '../../../components/ui/Dialog'
import { cn } from '../../../lib/cn'
import { formatFileSize, formatListTime } from '../../../lib/format'
import { saveFile } from '../files/openFiles'
import { useChatStore } from '../store/chatStore'
import type { Message } from '../types'
import { ImageViewer } from './ImageViewer'
import { ViewerButton } from './ViewerButton'
import { indexOfItem, neighbour, viewerItemsOf } from './viewerItems'
import { useViewerStore } from './viewerStore'

const NO_MESSAGES: Message[] = []

// The PDF renderer brings pdf.js and its worker, so it is fetched when a PDF is first opened and not with the viewer.
const PdfViewer = lazy(() => import('./PdfViewer').then((module) => ({ default: module.PdfViewer })))

// The full-screen viewer of the pictures and the PDFs of a conversation. It shows the file the viewer store names, and moves through the
// files of the messages that are loaded (the ones the list shows). A kind of file gets its own renderer here when it has one.
export function FileViewer({ conversationId }: { conversationId: string }) {
  const target = useViewerStore((state) => state.target)
  const open = useViewerStore((state) => state.open)
  const close = useViewerStore((state) => state.close)
  const messages = useChatStore((state) => state.messages[conversationId]) ?? NO_MESSAGES
  const items = useMemo(() => viewerItemsOf(messages), [messages])

  const attachmentId = target?.conversationId === conversationId ? target.attachmentId : null
  const index = attachmentId === null ? -1 : indexOfItem(items, attachmentId)
  const current = index >= 0 ? items[index] : null
  const previous = attachmentId === null ? null : neighbour(items, attachmentId, -1)
  const next = attachmentId === null ? null : neighbour(items, attachmentId, 1)

  const step = useCallback(
    (direction: 1 | -1) => {
      const to = direction === 1 ? next : previous
      if (to) open(conversationId, to.source.id)
    },
    [next, previous, open, conversationId],
  )

  // The picture shown can leave the list (its message was deleted, or edited so that the file is gone): the viewer goes to the one in
  // its place, or closes when there is none.
  const lastIndex = useRef(0)
  useEffect(() => {
    if (attachmentId === null) return
    if (index >= 0) {
      lastIndex.current = index
      return
    }

    const replacement = items[Math.min(lastIndex.current, items.length - 1)]
    if (replacement) open(conversationId, replacement.source.id)
    else close()
  }, [attachmentId, index, items, open, close, conversationId])

  useEffect(() => {
    if (attachmentId === null) return

    const onKey = (event: KeyboardEvent) => {
      if (event.ctrlKey || event.metaKey || event.altKey) return
      if (event.key === 'ArrowLeft') step(-1)
      else if (event.key === 'ArrowRight') step(1)
      else return

      event.preventDefault()
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [attachmentId, step])

  const [saving, setSaving] = useState(false)
  const [failedFor, setFailedFor] = useState<string | null>(null)

  function save() {
    if (!current) return

    const id = current.source.id
    setSaving(true)
    setFailedFor(null)
    saveFile(current.source)
      .catch(() => setFailedFor(id))
      .finally(() => setSaving(false))
  }

  const saveFailed = current !== null && failedFor === current.source.id

  return (
    <Dialog open={attachmentId !== null} onClose={close} title={current?.name ?? 'File'} variant="fullscreen">
      {current && (
        <div className="flex h-dvh flex-col">
          <header className="flex shrink-0 items-center gap-2 border-b border-white/10 px-3 py-2">
            <div className="min-w-0 flex-1">
              <h2 className="truncate text-sm font-medium">{current.name}</h2>
              <p role={saveFailed ? 'alert' : undefined} className={cn('truncate text-xs', saveFailed ? 'text-red-400' : 'text-slate-300')}>
                {saveFailed
                  ? "Couldn't download it. Try again."
                  : `${current.senderName} · ${formatListTime(current.sentAt, new Date())} · ${formatFileSize(current.size)}`}
              </p>
            </div>
            <span aria-live="polite" className="shrink-0 text-xs text-slate-300">
              {index + 1} of {items.length}
            </span>
            <ViewerButton aria-label="Download" isLoading={saving} onClick={save}>
              {!saving && <Download className="h-5 w-5" />}
            </ViewerButton>
            <ViewerButton aria-label="Close" onClick={close}>
              <X className="h-5 w-5" />
            </ViewerButton>
          </header>

          <div className="relative flex min-h-0 flex-1">
            {/* Keyed by the file, so that each one starts fitted and the one before it is let go of. */}
            {current.kind === 'Pdf' ? (
              <Suspense
                fallback={
                  <div role="status" aria-label={`Loading ${current.name}`} className="flex flex-1 items-center justify-center">
                    <LoaderCircle className="h-8 w-8 animate-spin text-slate-300" aria-hidden="true" />
                  </div>
                }
              >
                <PdfViewer key={current.source.id} source={current.source} name={current.name} onDownload={save} />
              </Suspense>
            ) : (
              <ImageViewer key={current.source.id} source={current.source} name={current.name} onDownload={save} onStep={step} />
            )}
            {previous && (
              <ViewerButton aria-label="Previous file" className="absolute left-2 top-1/2 -translate-y-1/2 bg-slate-800/70" onClick={() => step(-1)}>
                <ChevronLeft className="h-6 w-6" />
              </ViewerButton>
            )}
            {next && (
              <ViewerButton aria-label="Next file" className="absolute right-2 top-1/2 -translate-y-1/2 bg-slate-800/70" onClick={() => step(1)}>
                <ChevronRight className="h-6 w-6" />
              </ViewerButton>
            )}
          </div>
        </div>
      )}
    </Dialog>
  )
}
