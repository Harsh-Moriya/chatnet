import { AlertCircle, LoaderCircle, MoveHorizontal, ZoomIn, ZoomOut } from 'lucide-react'
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { Button } from '../../../components/ui/Button'
import { Input } from '../../../components/ui/Input'
import type { FileSource } from '../files/fileObjects'
import { useFileObject } from '../hooks/useFileObject'
import { type PdfDocument, type PdfProblem, PdfError, openPdf } from './pdfjs'
import { PdfPage } from './PdfPage'
import { CSS_UNITS, type Place, type Size, clampScale, fitWidthScale, lineOf, locate, pageOffsets, percentOf, stepScale } from './pdfView'
import { ViewerButton } from './ViewerButton'

// The space above the first page and under each page, and beside the pages when they are fitted to the width, in CSS pixels.
const GAP = 12
const SIDE_MARGIN = 16

type Loaded = { status: 'loading' } | { status: 'ready'; pdf: PdfDocument; first: Size } | { status: 'problem'; problem: PdfProblem }

const PROBLEMS: Record<PdfProblem, string> = {
  password: "This PDF is protected by a password, which can't be asked for here.",
  invalid: "This file isn't a PDF that can be shown.",
  failed: "Couldn't open this PDF.",
}

const LIGHT_BUTTON = 'text-white hover:bg-white/15 active:bg-white/25 focus-visible:ring-offset-slate-900'

interface PdfViewerProps {
  source: FileSource
  name: string
  onDownload: () => void
}

// One PDF: its pages in a column that scrolls, drawn as they come near the screen, with a toolbar for the page and the zoom. The file is read
// from the object URL the cache makes for it, in pdf.js's worker. The maths is in pdfView.ts and the talk with pdf.js in pdfjs.ts.
export function PdfViewer({ source, name, onDownload }: PdfViewerProps) {
  const [file, retryFile] = useFileObject(source, true)
  const url = file.status === 'ready' ? file.url : null

  const [loaded, setLoaded] = useState<Loaded>({ status: 'loading' })
  const [attempt, setAttempt] = useState(0)

  useEffect(() => {
    if (url === null) return

    let current = true
    let destroy: (() => void) | null = null

    void (async () => {
      try {
        const opened = await openPdf(url)
        destroy = opened.destroy

        const page = await opened.document.getPage(1)
        const viewport = page.getViewport({ scale: 1 })
        page.cleanup()
        if (current) setLoaded({ status: 'ready', pdf: opened.document, first: { width: viewport.width, height: viewport.height } })
      } catch (error) {
        if (current) setLoaded({ status: 'problem', problem: error instanceof PdfError ? error.problem : 'failed' })
      }
    })()

    return () => {
      current = false
      destroy?.()
    }
  }, [url, attempt])

  function retry() {
    setLoaded({ status: 'loading' })
    setAttempt((n) => n + 1)
    retryFile()
  }

  const scrollerRef = useRef<HTMLDivElement>(null)
  // The same box as an element in state, for the pages to measure their distance to the screen from (a ref does not re-render when it is set).
  const [scroller, setScroller] = useState<HTMLDivElement | null>(null)
  const attachScroller = useCallback((element: HTMLDivElement | null) => {
    scrollerRef.current = element
    setScroller(element)
  }, [])
  const [stageWidth, setStageWidth] = useState<number | null>(null)
  // The scale when it is not the fit to the width. Null is the fit, which follows the width of the stage as it changes.
  const [zoom, setZoom] = useState<number | null>(null)
  const [sizes, setSizes] = useState<Record<number, Size>>({})
  const [current, setCurrent] = useState(1)
  const [draft, setDraft] = useState<string | null>(null)

  const ready = loaded.status === 'ready' ? loaded : null
  const count = ready?.pdf.numPages ?? 0
  const first = ready?.first
  const fit = first && stageWidth ? fitWidthScale(first.width, stageWidth, SIDE_MARGIN) : CSS_UNITS
  const scale = zoom ?? fit

  const heights = useMemo(
    () => Array.from({ length: count }, (_, index) => (sizes[index + 1] ?? first ?? { width: 0, height: 0 }).height * scale),
    [count, sizes, first, scale],
  )
  const offsets = useMemo(() => pageOffsets(heights, GAP), [heights])

  const onSize = useCallback((number: number, size: Size) => {
    setSizes((all) => (all[number]?.width === size.width && all[number]?.height === size.height ? all : { ...all, [number]: size }))
  }, [])

  // A change of scale keeps the part of the document at the top of the view where it is: the place is noted before the change and the
  // scroll is put back to it once the pages have their new sizes.
  const anchor = useRef<Place | null>(null)
  useLayoutEffect(() => {
    const scroller = scrollerRef.current
    const place = anchor.current
    if (!scroller || !place || offsets.length === 0) return

    anchor.current = null
    scroller.scrollTop = lineOf(offsets, heights, place)
  })

  // What the handlers below see, which are made once.
  const live = useRef({ scale, fit, offsets, heights })
  useEffect(() => {
    live.current = { scale, fit, offsets, heights }
  })

  const setScale = useCallback((next: number | null) => {
    const scroller = scrollerRef.current
    const { offsets: lines, heights: sizesNow, fit: fitNow } = live.current
    // The place is noted only for the first of several changes before the next render, so it stays the place the person was looking at.
    if (scroller && lines.length > 0 && anchor.current === null) anchor.current = locate(lines, sizesNow, scroller.scrollTop)

    const scale = next === null ? null : clampScale(next)
    // The scale the next change starts from, before the page has been drawn again: a pinch on a trackpad sends several in one frame.
    live.current = { ...live.current, scale: scale ?? fitNow }
    setZoom(scale)
  }, [])
  const zoomBy = useCallback((direction: 1 | -1) => setScale(stepScale(live.current.scale, direction)), [setScale])
  const fitToWidth = useCallback(() => setScale(null), [setScale])

  useEffect(() => {
    const element = scrollerRef.current
    if (!element) return

    const observer = new ResizeObserver(([entry]) => setStageWidth(entry.contentRect.width))
    observer.observe(element)
    return () => observer.disconnect()
  }, [])

  // A native listener, because a ctrl or cmd and wheel has to be kept from zooming the whole page and React's own cannot be.
  useEffect(() => {
    const element = scrollerRef.current
    if (!element) return

    const onWheel = (event: WheelEvent) => {
      if (!event.ctrlKey && !event.metaKey) return

      event.preventDefault()
      zoomBy(event.deltaY < 0 ? 1 : -1)
    }
    element.addEventListener('wheel', onWheel, { passive: false })
    return () => element.removeEventListener('wheel', onWheel)
  }, [zoomBy])

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.ctrlKey || event.metaKey || event.altKey) return
      // Typing in the page field is not a command.
      if (event.target instanceof HTMLInputElement) return

      if (event.key === '+' || event.key === '=') zoomBy(1)
      else if (event.key === '-') zoomBy(-1)
      else if (event.key === '0') fitToWidth()
      else return

      event.preventDefault()
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [zoomBy, fitToWidth])

  // The column takes the keys of scrolling (space, page up and down, the arrows) once the viewer is open.
  useEffect(() => {
    const frame = requestAnimationFrame(() => scrollerRef.current?.focus({ preventScroll: true }))
    return () => cancelAnimationFrame(frame)
  }, [])

  function onScroll() {
    const scroller = scrollerRef.current
    if (!scroller || offsets.length === 0) return

    const place = locate(offsets, heights, scroller.scrollTop + scroller.clientHeight / 2)
    setCurrent(place.index + 1)
  }

  function goToPage() {
    const scroller = scrollerRef.current
    const wanted = Number.parseInt(draft ?? '', 10)
    setDraft(null)
    if (!scroller || Number.isNaN(wanted) || count === 0) return

    const number = Math.min(count, Math.max(1, wanted))
    scroller.scrollTo({ top: offsets[number - 1] - GAP })
    setCurrent(number)
    // Back to the column, so that the keys of scrolling go on working.
    scroller.focus({ preventScroll: true })
  }

  const problem = loaded.status === 'problem' ? PROBLEMS[loaded.problem] : file.status === 'failed' ? "Couldn't load this PDF." : null
  const canRetry = file.status === 'failed' || (loaded.status === 'problem' && loaded.problem === 'failed')

  return (
    <div className="flex min-h-0 min-w-0 flex-1 flex-col">
      {ready && (
        <div className="flex shrink-0 items-center justify-center gap-1 border-b border-white/10 px-2 py-1">
          <span className="text-xs text-slate-300">Page</span>
          <div className="w-14">
            <Input
              aria-label="Page number"
              inputMode="numeric"
              className="h-8 px-2 text-center"
              value={draft ?? String(current)}
              onChange={(event) => setDraft(event.target.value)}
              onFocus={(event) => event.target.select()}
              onBlur={goToPage}
              onKeyDown={(event) => {
                if (event.key === 'Enter') {
                  event.currentTarget.blur()
                } else if (event.key === 'Escape') {
                  // Gives up the edit, and does not also close the viewer.
                  event.preventDefault()
                  setDraft(null)
                  scrollerRef.current?.focus({ preventScroll: true })
                }
              }}
            />
          </div>
          <span className="mr-2 text-xs text-slate-300">of {count}</span>
          <ViewerButton aria-label="Zoom out" onClick={() => zoomBy(-1)}>
            <ZoomOut className="h-5 w-5" />
          </ViewerButton>
          <span aria-label="Zoom" className="min-w-12 text-center text-xs text-white">
            {percentOf(scale)}%
          </span>
          <ViewerButton aria-label="Zoom in" onClick={() => zoomBy(1)}>
            <ZoomIn className="h-5 w-5" />
          </ViewerButton>
          <ViewerButton aria-label="Fit to width" onClick={fitToWidth}>
            <MoveHorizontal className="h-5 w-5" />
          </ViewerButton>
        </div>
      )}

      <div
        ref={attachScroller}
        tabIndex={0}
        aria-label={`${name}, pages`}
        onScroll={onScroll}
        className="relative min-h-0 flex-1 overflow-auto bg-slate-800 outline-none"
      >
        {ready && (
          <div className="w-max min-w-full" style={{ paddingTop: GAP }}>
            {Array.from({ length: count }, (_, index) => (
              <PdfPage
                key={index + 1}
                pdf={ready.pdf}
                number={index + 1}
                count={count}
                scale={scale}
                gap={GAP}
                size={sizes[index + 1] ?? ready.first}
                onSize={onSize}
                scroller={scroller}
              />
            ))}
          </div>
        )}

        {!problem && !ready && (
          <div role="status" aria-label={`Loading ${name}`} className="absolute inset-0 flex items-center justify-center">
            <LoaderCircle className="h-8 w-8 animate-spin text-slate-300" aria-hidden="true" />
          </div>
        )}

        {problem && (
          <div role="alert" className="absolute inset-0 flex flex-col items-center justify-center gap-3 p-6 text-center">
            <AlertCircle className="h-8 w-8 text-slate-300" aria-hidden="true" />
            <p className="max-w-xs text-sm text-slate-300">{problem}</p>
            <div className="flex gap-2">
              {canRetry && (
                <Button variant="ghost" size="sm" className={LIGHT_BUTTON} onClick={retry}>
                  Try again
                </Button>
              )}
              <Button variant="ghost" size="sm" className={LIGHT_BUTTON} onClick={onDownload}>
                Download
              </Button>
            </div>
          </div>
        )}
      </div>
    </div>
  )
}
