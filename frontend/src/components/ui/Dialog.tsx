import { type ReactNode, useEffect, useRef } from 'react'
import { X } from 'lucide-react'
import { Button } from './Button'

interface DialogProps {
  open: boolean
  onClose: () => void
  title: string
  children: ReactNode
  // `fullscreen` fills the screen and draws no box or header of its own, for content that brings its own controls (the file viewer).
  variant?: 'default' | 'fullscreen'
}

// A modal built on the browser's own <dialog> element, which already provides what a hand-made
// modal has to imitate: it traps focus, closes on Escape, makes the page behind it inert, and
// returns focus to what opened it. The content is only rendered while open, so a form inside
// starts empty every time.
export function Dialog({ open, onClose, title, children, variant = 'default' }: DialogProps) {
  const ref = useRef<HTMLDialogElement>(null)

  useEffect(() => {
    const dialog = ref.current
    if (!dialog) return

    if (open && !dialog.open) dialog.showModal()
    if (!open && dialog.open) dialog.close()
  }, [open])

  return (
    <dialog
      ref={ref}
      aria-label={title}
      onClose={onClose}
      // A click on the dim area outside the box lands on the <dialog> itself.
      onClick={(event) => {
        if (event.target === event.currentTarget) onClose()
      }}
      className={
        variant === 'fullscreen'
          ? 'm-0 h-dvh max-h-none w-dvw max-w-none rounded-none border-0 bg-slate-900 p-0 text-white backdrop:bg-slate-900'
          : 'm-auto w-[calc(100%-2rem)] max-w-md rounded-lg border border-line bg-white p-0 shadow-xl backdrop:bg-slate-900/40'
      }
    >
      {open && variant === 'fullscreen' && children}
      {open && variant === 'default' && (
        <div className="flex max-h-[85dvh] flex-col">
          <div className="flex items-center justify-between border-b border-line px-5 py-4">
            <h2 className="text-base font-semibold text-slate-900">{title}</h2>
            <Button variant="ghost" size="icon" aria-label="Close" onClick={onClose}>
              <X className="h-5 w-5" />
            </Button>
          </div>
          {children}
        </div>
      )}
    </dialog>
  )
}
