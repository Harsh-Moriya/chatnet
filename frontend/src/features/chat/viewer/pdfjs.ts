import {
  GlobalWorkerOptions,
  InvalidPDFException,
  PasswordException,
  RenderingCancelledException,
  getDocument,
  type PDFDocumentProxy,
  type PDFPageProxy,
} from 'pdfjs-dist'
import workerUrl from 'pdfjs-dist/build/pdf.worker.min.mjs?url'
import { outputScale } from './pdfView'

// The only file that talks to pdf.js, so that the rest of the viewer never sees its types, and so that the library, its worker and its data
// are loaded only when a PDF is opened (the viewer that imports this file is loaded on demand).
//
// What a PDF can do here is deliberately small: pages are drawn to canvas and that is all. There is no text layer, no annotation layer (so no
// links and no forms), no scripting and no XFA; the file cannot reach anything but this page's own copy of it.

export type { PDFDocumentProxy as PdfDocument, PDFPageProxy as PdfPageProxy }

// The worker is a file of this site, built under /assets with the rest.
GlobalWorkerOptions.workerSrc = workerUrl

// The data pdf.js reads while drawing: the character maps of fonts that are not in the PDF (mostly Chinese, Japanese and Korean ones), and
// the decoders of three kinds of image (JPEG 2000, JBIG2 scans and ICC colour profiles). Vite builds each as a file under /assets with a hashed
// name, so what pdf.js asks for by name is looked up here, and each is fetched only when a PDF needs it. Standard font files are not shipped: a
// font that is not in the PDF falls back to one of the system.
const bundled = import.meta.glob('/node_modules/pdfjs-dist/{cmaps/*.bcmap,wasm/{openjpeg,jbig2,qcms_bg}.wasm}', {
  query: '?url&no-inline',
  import: 'default',
  eager: true,
}) as Record<string, string>

const FOLDERS: Record<string, string> = { cMapUrl: 'cmaps', wasmUrl: 'wasm' }

class BundledBinaryData {
  async fetch({ kind, filename }: { kind: string; filename: string }): Promise<Uint8Array> {
    const url = bundled[`/node_modules/pdfjs-dist/${FOLDERS[kind]}/${filename}`]
    if (!url) throw new Error(`pdf.js asked for ${kind} ${filename}, which is not shipped.`)

    const response = await fetch(url, { credentials: 'omit' })
    if (!response.ok) throw new Error(`The data of pdf.js answered ${response.status}.`)
    return new Uint8Array(await response.arrayBuffer())
  }
}

// What is wrong with a PDF that cannot be shown: it asks for a password (which this viewer does not take), it is not a PDF, or it could not
// be read for another reason.
export type PdfProblem = 'password' | 'invalid' | 'failed'

export class PdfError extends Error {
  readonly problem: PdfProblem

  constructor(problem: PdfProblem) {
    super(`The PDF cannot be shown: ${problem}.`)
    this.name = 'PdfError'
    this.problem = problem
  }
}

export interface OpenedPdf {
  document: PDFDocumentProxy
  destroy: () => void
}

function problemOf(error: unknown): PdfProblem {
  if (error instanceof PasswordException) return 'password'
  if (error instanceof InvalidPDFException) return 'invalid'
  return 'failed'
}

// Opens the PDF at an address (the object URL of the decrypted file). pdf.js reads it in its worker, so its bytes do not pass through the page.
export async function openPdf(url: string): Promise<OpenedPdf> {
  const task = getDocument({
    url,
    BinaryDataFactory: BundledBinaryData,
    useWorkerFetch: false,
    enableXfa: false,
    // Errors only: a missing standard font, which falls back, is not worth a line in the console.
    verbosity: 0,
  })

  try {
    const document = await task.promise
    return { document, destroy: () => void task.destroy() }
  } catch (error) {
    void task.destroy()
    throw new PdfError(problemOf(error))
  }
}

export interface Drawing {
  done: Promise<void>
  cancel: () => void
}

export function isCancelled(error: unknown): boolean {
  return error instanceof RenderingCancelledException
}

// Draws a page onto `target` at `scale` (CSS pixels for each point of the page), as sharp as the screen's pixel ratio allows and the canvas may
// be. It is drawn on a canvas of its own and copied over when it is complete, so what is on `target` stays until then.
export function drawPage(page: PDFPageProxy, target: HTMLCanvasElement, scale: number, pixelRatio: number): Drawing {
  const shown = page.getViewport({ scale })
  const sharp = outputScale(pixelRatio, shown)
  const viewport = page.getViewport({ scale: scale * sharp })

  const canvas = document.createElement('canvas')
  canvas.width = Math.ceil(viewport.width)
  canvas.height = Math.ceil(viewport.height)
  const context = canvas.getContext('2d')
  if (!context) return { done: Promise.reject(new Error('A canvas cannot be drawn on.')), cancel: () => undefined }

  const task = page.render({ canvas, canvasContext: context, viewport })
  const done = task.promise
    .then(() => {
      target.width = canvas.width
      target.height = canvas.height
      target.getContext('2d')?.drawImage(canvas, 0, 0)
    })
    .finally(() => {
      canvas.width = 0
      canvas.height = 0
    })

  return { done, cancel: () => task.cancel() }
}
