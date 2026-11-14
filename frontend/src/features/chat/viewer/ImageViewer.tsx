import { AlertCircle, LoaderCircle, Maximize, ZoomIn, ZoomOut } from 'lucide-react'
import { type PointerEvent, useCallback, useEffect, useRef, useState } from 'react'
import { Button } from '../../../components/ui/Button'
import { cn } from '../../../lib/cn'
import type { FileSource } from '../files/fileObjects'
import { useFileObject } from '../hooks/useFileObject'
import {
  ACTUAL_SIZE,
  BUTTON_STEP,
  type Frame,
  type Point,
  type Size,
  type View,
  clampView,
  fitScale,
  fitView,
  panBy,
  toggleZoom,
  wheelFactor,
  zoomAt,
} from './imageView'
import { ViewerButton } from './ViewerButton'

// A swipe along the picture goes to the next or the previous one when it is at least this long (in pixels) and mostly sideways.
const SWIPE_DISTANCE = 60
const SWIPE_RATIO = 1.5
// Two taps this close in time (ms) and place (pixels) are a double tap, and a touch that moved less than the slop is a tap.
const DOUBLE_TAP_MS = 300
const DOUBLE_TAP_DISTANCE = 30
const TAP_SLOP = 8
// A turn of the wheel counted in lines is worth this many pixels.
const LINE_PIXELS = 16

interface ImageViewerProps {
  source: FileSource
  name: string
  onDownload: () => void
  // Asks for the next (1) or the previous (-1) picture, by a swipe.
  onStep: (step: 1 | -1) => void
}

const distance = (a: Point, b: Point) => Math.hypot(a.x - b.x, a.y - b.y)
const middle = (a: Point, b: Point): Point => ({ x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 })

// One picture, opened and shown on a stage that fills what the toolbar leaves. It starts fitted; the wheel, a pinch, the buttons and the
// keys zoom it, a drag moves it when it is zoomed, a double click or a double tap toggles the zoom. The maths is in imageView.ts.
export function ImageViewer({ source, name, onDownload, onStep }: ImageViewerProps) {
  const [file, retry] = useFileObject(source, true)
  const [drawFailed, setDrawFailed] = useState(false)
  const stageRef = useRef<HTMLDivElement>(null)
  const [stage, setStage] = useState<Size | null>(null)
  const [natural, setNatural] = useState<Size | null>(null)
  // The view when it is not the fit. Null is the fit, which follows the size of the stage as it changes.
  const [zoomed, setZoomed] = useState<View | null>(null)

  const frame: Frame | null = stage && natural ? { stage, natural } : null
  const view = frame ? (zoomed ? clampView(zoomed, frame) : fitView(frame)) : null
  const isZoomedIn = view !== null && frame !== null && view.scale > fitScale(frame) + 1e-9

  // What the handlers below see, which are made once and so cannot close over the latest render.
  const live = useRef<{ view: View | null; frame: Frame | null }>({ view: null, frame: null })
  useEffect(() => {
    live.current = { view, frame }
  })

  const commit = useCallback((next: View) => {
    const current = live.current.frame
    if (!current) return
    setZoomed(next.scale <= fitScale(current) + 1e-9 ? null : next)
  }, [])

  // A point of the screen as a point of the stage, from its centre.
  const pointOf = useCallback((clientX: number, clientY: number): Point => {
    const rect = stageRef.current?.getBoundingClientRect()
    return rect ? { x: clientX - (rect.left + rect.width / 2), y: clientY - (rect.top + rect.height / 2) } : { x: 0, y: 0 }
  }, [])

  useEffect(() => {
    const element = stageRef.current
    if (!element) return

    const observer = new ResizeObserver(([entry]) => setStage({ width: entry.contentRect.width, height: entry.contentRect.height }))
    observer.observe(element)
    return () => observer.disconnect()
  }, [])

  // A native listener, because the wheel has to be stopped from scrolling the page and React's own cannot be.
  useEffect(() => {
    const element = stageRef.current
    if (!element) return

    const onWheel = (event: WheelEvent) => {
      const { view: current, frame: size } = live.current
      if (!current || !size) return

      event.preventDefault()
      const delta = event.deltaMode === 1 ? event.deltaY * LINE_PIXELS : event.deltaY
      commit(zoomAt(current, wheelFactor(delta), pointOf(event.clientX, event.clientY), size))
    }
    element.addEventListener('wheel', onWheel, { passive: false })
    return () => element.removeEventListener('wheel', onWheel)
  }, [commit, pointOf])

  const zoomBy = useCallback(
    (factor: number) => {
      const { view: current, frame: size } = live.current
      if (current && size) commit(zoomAt(current, factor, { x: 0, y: 0 }, size))
    },
    [commit],
  )
  const fit = useCallback(() => setZoomed(null), [])
  const actualSize = useCallback(() => commit(ACTUAL_SIZE), [commit])

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.ctrlKey || event.metaKey || event.altKey) return

      if (event.key === '+' || event.key === '=') zoomBy(BUTTON_STEP)
      else if (event.key === '-') zoomBy(1 / BUTTON_STEP)
      else if (event.key === '0') fit()
      else if (event.key === '1') actualSize()
      else return

      event.preventDefault()
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [zoomBy, fit, actualSize])

  // Where each finger or the mouse is, in screen pixels, and how far the first has moved since it went down (for a swipe or a tap).
  const pointers = useRef(new Map<number, Point>())
  const travelled = useRef({ dx: 0, dy: 0 })
  const lastTap = useRef<{ time: number; point: Point } | null>(null)
  const lastType = useRef('mouse')

  function onPointerDown(event: PointerEvent<HTMLDivElement>) {
    event.currentTarget.setPointerCapture(event.pointerId)
    lastType.current = event.pointerType
    pointers.current.set(event.pointerId, { x: event.clientX, y: event.clientY })
    if (pointers.current.size === 1) travelled.current = { dx: 0, dy: 0 }
  }

  function onPointerMove(event: PointerEvent<HTMLDivElement>) {
    const previous = pointers.current.get(event.pointerId)
    if (!previous) return

    const { view: current, frame: size } = live.current
    const now = { x: event.clientX, y: event.clientY }

    if (pointers.current.size === 2) {
      const other = [...pointers.current.entries()].find(([id]) => id !== event.pointerId)?.[1]
      if (current && size && other && distance(previous, other) > 0) {
        const before = middle(previous, other)
        const after = middle(now, other)
        const pinched = zoomAt(current, distance(now, other) / distance(previous, other), pointOf(before.x, before.y), size)
        commit(panBy(pinched, after.x - before.x, after.y - before.y, size))
      }
    } else if (pointers.current.size === 1) {
      travelled.current = { dx: travelled.current.dx + now.x - previous.x, dy: travelled.current.dy + now.y - previous.y }
      if (current && size && current.scale > fitScale(size) + 1e-9) commit(panBy(current, now.x - previous.x, now.y - previous.y, size))
    }

    pointers.current.set(event.pointerId, now)
  }

  function onPointerUp(event: PointerEvent<HTMLDivElement>) {
    const wasAlone = pointers.current.size === 1
    pointers.current.delete(event.pointerId)
    if (!wasAlone) return

    const { view: current, frame: size } = live.current
    const { dx, dy } = travelled.current
    // A picture that is not shown yet has nothing to zoom, so a swipe on it moves on, which lets someone page quickly through pictures.
    const atFit = !current || !size || current.scale <= fitScale(size) + 1e-9

    if (atFit && Math.abs(dx) >= SWIPE_DISTANCE && Math.abs(dx) > SWIPE_RATIO * Math.abs(dy)) {
      onStep(dx < 0 ? 1 : -1)
      return
    }

    if (!current || !size) return

    // A touch has no double click of its own that can be relied on, so two quick taps are looked for here.
    if (event.pointerType === 'touch' && Math.hypot(dx, dy) < TAP_SLOP) {
      const point = pointOf(event.clientX, event.clientY)
      const last = lastTap.current
      if (last && event.timeStamp - last.time < DOUBLE_TAP_MS && distance(point, last.point) < DOUBLE_TAP_DISTANCE) {
        commit(toggleZoom(current, point, size))
        lastTap.current = null
      } else {
        lastTap.current = { time: event.timeStamp, point }
      }
    }
  }

  function onDoubleClick(event: React.MouseEvent<HTMLDivElement>) {
    const { view: current, frame: size } = live.current
    // A double tap is handled above; the double click some touch browsers also make would undo it.
    if (!current || !size || lastType.current === 'touch') return
    commit(toggleZoom(current, pointOf(event.clientX, event.clientY), size))
  }

  const failed = file.status === 'failed' || drawFailed

  return (
    <div
      ref={stageRef}
      className={cn('relative min-w-0 flex-1 touch-none select-none overflow-hidden', isZoomedIn && 'cursor-grab active:cursor-grabbing')}
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={onPointerUp}
      onPointerCancel={(event) => pointers.current.delete(event.pointerId)}
      onDoubleClick={onDoubleClick}
    >
      {file.status === 'ready' && !drawFailed && (
        <img
          src={file.url}
          alt={name}
          draggable={false}
          onLoad={(event) => setNatural({ width: event.currentTarget.naturalWidth, height: event.currentTarget.naturalHeight })}
          onError={() => setDrawFailed(true)}
          className={cn('pointer-events-none absolute left-1/2 top-1/2 max-w-none', !view && 'opacity-0')}
          style={{
            width: natural?.width,
            height: natural?.height,
            transform: view ? `translate(calc(-50% + ${view.x}px), calc(-50% + ${view.y}px)) scale(${view.scale})` : undefined,
          }}
        />
      )}

      {!failed && !view && (
        <div role="status" aria-label={`Loading ${name}`} className="absolute inset-0 flex items-center justify-center">
          <LoaderCircle className="h-8 w-8 animate-spin text-slate-300" aria-hidden="true" />
        </div>
      )}

      {failed && (
        <div role="alert" className="absolute inset-0 flex flex-col items-center justify-center gap-3 p-6 text-center">
          <AlertCircle className="h-8 w-8 text-slate-300" aria-hidden="true" />
          <p className="max-w-xs text-sm text-slate-300">{drawFailed ? "This picture can't be shown here." : "Couldn't load this picture."}</p>
          <div className="flex gap-2">
            {!drawFailed && (
              <Button variant="ghost" size="sm" className="text-white hover:bg-white/15 active:bg-white/25 focus-visible:ring-offset-slate-900" onClick={retry}>
                Try again
              </Button>
            )}
            <Button variant="ghost" size="sm" className="text-white hover:bg-white/15 active:bg-white/25 focus-visible:ring-offset-slate-900" onClick={onDownload}>
              Download
            </Button>
          </div>
        </div>
      )}

      {view && !failed && (
        // The controls sit on the stage, so a press on them must not start a drag of the picture, which would swallow the click.
        <div
          className="absolute bottom-4 left-1/2 flex -translate-x-1/2 items-center gap-1 rounded-lg bg-slate-800/80 p-1"
          onPointerDown={(event) => event.stopPropagation()}
          onDoubleClick={(event) => event.stopPropagation()}
        >
          <ViewerButton aria-label="Zoom out" onClick={() => zoomBy(1 / BUTTON_STEP)}>
            <ZoomOut className="h-5 w-5" />
          </ViewerButton>
          <Button
            variant="ghost"
            size="sm"
            aria-label="Actual size"
            className="min-w-14 text-white hover:bg-white/15 active:bg-white/25 focus-visible:ring-offset-slate-900"
            onClick={actualSize}
          >
            {Math.round(view.scale * 100)}%
          </Button>
          <ViewerButton aria-label="Zoom in" onClick={() => zoomBy(BUTTON_STEP)}>
            <ZoomIn className="h-5 w-5" />
          </ViewerButton>
          <ViewerButton aria-label="Fit to screen" onClick={fit}>
            <Maximize className="h-5 w-5" />
          </ViewerButton>
        </div>
      )}
    </div>
  )
}
