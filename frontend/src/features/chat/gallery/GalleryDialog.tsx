import { useInfiniteQuery } from '@tanstack/react-query'
import { AlertCircle, FileText, Image as ImageIcon, LoaderCircle, Play } from 'lucide-react'
import { useEffect, useMemo, useState } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Dialog } from '../../../components/ui/Dialog'
import { cn } from '../../../lib/cn'
import { getApiError } from '../../../lib/errors'
import { formatFileSize } from '../../../lib/format'
import { isInlinePicture } from '../files/fileNames'
import { useFileObject } from '../hooks/useFileObject'
import { useInView } from '../hooks/useInView'
import { useChatStore } from '../store/chatStore'
import type { ViewerItem } from '../viewer/viewerItems'
import { useViewerStore } from '../viewer/viewerStore'
import { deletedIdsOf, galleryItemsOf } from './galleryItems'
import { galleryQueryOptions } from './galleryQuery'

interface GalleryDialogProps {
  conversationId: string
  open: boolean
  onClose: () => void
}

// Every picture, video and PDF of a conversation, newest first, read a page at a time from the server as the grid is scrolled. The list is
// only fetched while the dialog is open (the content is rendered then), so each opening starts from the newest page.
export function GalleryDialog({ conversationId, open, onClose }: GalleryDialogProps) {
  return (
    <Dialog open={open} onClose={onClose} title="Pictures, videos and PDFs" variant="wide">
      <GalleryContent conversationId={conversationId} />
    </Dialog>
  )
}

function GalleryContent({ conversationId }: { conversationId: string }) {
  const query = useInfiniteQuery(galleryQueryOptions(conversationId))
  const held = useChatStore((state) => state.messages[conversationId])
  const items = useMemo(() => galleryItemsOf(query.data?.pages ?? [], deletedIdsOf(held)), [query.data, held])
  const openViewer = useViewerStore((state) => state.open)

  // The next page is read when the end of the grid comes near. A page whose files cannot be shown adds no tile, so the end stays near and
  // the next one is read at once: that is how the grid walks past them. A failed page stops it until the person asks again.
  // The grid scrolls in a box of its own, so how near the end and the tiles are is measured from that box.
  const [scroller, setScroller] = useState<HTMLDivElement | null>(null)
  const [attachEnd, endNear] = useInView<HTMLDivElement>('400px', true, scroller)
  const { hasNextPage, isFetching, isFetchNextPageError, fetchNextPage } = query
  useEffect(() => {
    if (endNear && hasNextPage && !isFetching && !isFetchNextPageError) void fetchNextPage()
  }, [endNear, hasNextPage, isFetching, isFetchNextPageError, fetchNextPage])

  if (query.isPending) {
    return (
      <div role="status" aria-label="Loading" className="flex flex-1 items-center justify-center p-8">
        <LoaderCircle className="h-8 w-8 animate-spin text-fg-muted" aria-hidden="true" />
      </div>
    )
  }

  if (query.isError && !query.data) {
    return (
      <div className="p-4">
        <ErrorBanner message={getApiError(query.error)} onRetry={() => void query.refetch()} />
      </div>
    )
  }

  if (items.length === 0 && !hasNextPage) {
    return <p className="p-8 text-center text-sm text-fg-muted">No pictures, videos or PDFs here yet.</p>
  }

  return (
    <div ref={setScroller} className="min-h-0 flex-1 overflow-y-auto p-2">
      <ul className="grid grid-cols-3 gap-1 sm:grid-cols-4 md:grid-cols-5">
        {items.map((item) => (
          <li key={item.source.id}>
            <GalleryTile item={item} scroller={scroller} onOpen={() => openViewer(conversationId, item.source.id, 'gallery')} />
          </li>
        ))}
      </ul>

      {/* The end of the grid: when it comes near, the next page is read. */}
      <div ref={attachEnd} className="flex min-h-12 items-center justify-center py-3">
        {isFetchNextPageError ? (
          <ErrorBanner message={getApiError(query.error)} onRetry={() => void fetchNextPage()} />
        ) : hasNextPage ? (
          <LoaderCircle role="status" aria-label="Loading more" className="h-5 w-5 animate-spin text-fg-muted" />
        ) : null}
      </div>
    </div>
  )
}

function ErrorBanner({ message, onRetry }: { message: string; onRetry: () => void }) {
  return (
    <Alert
      role="alert"
      className="w-full"
      action={
        <Button variant="secondary" size="sm" onClick={onRetry}>
          Try again
        </Button>
      }
    >
      {message}
    </Alert>
  )
}

// A picture of a size the messages draw is drawn here as it comes near, and let go of when it is far again, so a long history does not keep
// every picture. A video, a PDF and a larger picture are a tile with its name, and nothing is fetched until it is opened.
function GalleryTile({ item, scroller, onOpen }: { item: ViewerItem; scroller: Element | null; onOpen: () => void }) {
  const thumbnail = isInlinePicture(item.source.entry)
  const [attach, near] = useInView<HTMLDivElement>('400px', true, scroller)
  const [state, retry] = useFileObject(thumbnail ? item.source : null, thumbnail && near)

  return (
    <div ref={attach} className="relative aspect-square overflow-hidden rounded-lg bg-subtle">
      {state.status === 'failed' ? (
        <Button variant="ghost" className="h-full w-full flex-col gap-1 rounded-lg px-1! py-1! text-xs text-fg-muted" onClick={retry}>
          <AlertCircle className="h-4 w-4" aria-hidden="true" />
          <span>Couldn&apos;t load. Try again.</span>
        </Button>
      ) : (
        <Button
          variant="ghost"
          aria-label={`Open ${item.name}`}
          className={cn('h-full w-full rounded-lg', state.status === 'ready' ? 'px-0! py-0! hover:bg-transparent active:bg-transparent' : 'flex-col gap-1 px-2! py-2! text-xs text-fg-muted')}
          onClick={onOpen}
        >
          {state.status === 'ready' ? (
            <img src={state.url} alt={item.name} decoding="async" className="h-full w-full object-cover" />
          ) : thumbnail ? (
            <span role="img" aria-label={`Loading ${item.name}`} className="h-full w-full animate-pulse" />
          ) : (
            <>
              <KindIcon kind={item.kind} />
              <span className="line-clamp-2 w-full break-words px-1 text-center font-medium text-fg">{item.name}</span>
              <span>{formatFileSize(item.size)}</span>
            </>
          )}
        </Button>
      )}
    </div>
  )
}

function KindIcon({ kind }: { kind: ViewerItem['kind'] }) {
  const className = 'h-7 w-7 shrink-0'
  if (kind === 'Video') return <Play className={className} aria-hidden="true" />
  if (kind === 'Pdf') return <FileText className={className} aria-hidden="true" />
  return <ImageIcon className={className} aria-hidden="true" />
}
