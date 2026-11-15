import { useEffect, useRef, useState } from 'react'
import { useInView } from '../hooks/useInView'
import { type PdfDocument, type PdfPageProxy, drawPage, isCancelled } from './pdfjs'
import type { Size } from './pdfView'

// How far from the screen, up and down, a page is still drawn: one screen.
const NEAR = '100% 0px'

interface PdfPageProps {
  pdf: PdfDocument
  // From 1.
  number: number
  count: number
  // CSS pixels for each point of the page.
  scale: number
  // The space under the page, in CSS pixels.
  gap: number
  // The size of the page in points: its own once it is known, the first page's until then.
  size: Size
  // Reports the real size of the page when it has been read.
  onSize: (number: number, size: Size) => void
  // The box the column scrolls in, which the distance to the screen is measured from; null until it is there.
  scroller: Element | null
}

// One page of the column. The box has its size from the start, so the column is as long as it will be and scrolling is steady; the page is
// drawn only while it is near the screen, and let go of when it is not, so a long document holds a handful of canvases.
export function PdfPage({ pdf, number, count, scale, gap, size, onSize, scroller }: PdfPageProps) {
  const [attach, near] = useInView<HTMLDivElement>(NEAR, true, scroller)
  const canvasRef = useRef<HTMLCanvasElement>(null)
  const pageRef = useRef<PdfPageProxy | null>(null)
  const [failed, setFailed] = useState(false)

  useEffect(() => {
    const canvas = canvasRef.current
    if (!near || !canvas) return

    let current = true
    let cancel: () => void = () => undefined

    void (async () => {
      try {
        const page = await pdf.getPage(number)
        if (!current) return

        pageRef.current = page
        const viewport = page.getViewport({ scale: 1 })
        onSize(number, { width: viewport.width, height: viewport.height })

        const drawing = drawPage(page, canvas, scale, window.devicePixelRatio)
        cancel = drawing.cancel
        await drawing.done
        if (current) setFailed(false)
      } catch (error) {
        if (current && !isCancelled(error)) setFailed(true)
      }
    })()

    return () => {
      current = false
      cancel()
    }
  }, [near, pdf, number, scale, onSize])

  // Out of reach: let go of the pixels and of what the page holds. A zoom does not come through here, so the old picture stays, stretched,
  // until the new one is drawn.
  useEffect(() => {
    if (near) return

    const canvas = canvasRef.current
    if (canvas) {
      canvas.width = 0
      canvas.height = 0
    }
    pageRef.current?.cleanup()
  }, [near])

  return (
    <div
      ref={attach}
      className="relative mx-auto bg-white shadow-lg"
      style={{ width: size.width * scale, height: size.height * scale, marginBottom: gap }}
    >
      <canvas ref={canvasRef} role="img" aria-label={`Page ${number} of ${count}`} className="block h-full w-full" />
      {failed && (
        <p role="alert" className="absolute inset-0 flex items-center justify-center p-4 text-center text-sm text-slate-500">
          This page can&apos;t be drawn.
        </p>
      )}
    </div>
  )
}
