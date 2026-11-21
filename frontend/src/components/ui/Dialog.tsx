import { type ReactNode, useEffect, useRef } from 'react'
import { X } from 'lucide-react'
import { cn } from '../../lib/cn'
import { Button } from './Button'

interface DialogProps {
  open: boolean
  onClose: () => void
  title: string
  children: ReactNode
  // `fullscreen` fills the screen and draws no box or header of its own, for content that brings its own controls (the file viewer).
  // `wide` is the default box made large, for content that scrolls inside it (the gallery); on a phone it fills the screen.
  variant?: 'default' | 'wide' | 'fullscreen'
  // False for a dialog that must be answered with its own buttons (a ringing call): Escape and a click outside do nothing.
  dismissible?: boolean
}

// A modal built on the browser's own <dialog> element, which already provides what a hand-made
// modal has to imitate: it traps focus, closes on Escape, makes the page behind it inert, and
// returns focus to what opened it. The content is only rendered while open, so a form inside
// starts empty every time.
export function Dialog({ open, onClose, title, children, variant = 'default', dismissible = true }: DialogProps) {
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
      onClose={() => {
        // A dialog that must be answered can still be closed by Escape: a browser lets a page cancel Escape only once per click, and not at
        // all for a dialog that opened without one (a ringing call). It is opened again, so the call never loses the only screen it has.
        const dialog = ref.current
        if (!dismissible && open && dialog && !dialog.open) {
          dialog.showModal()
          return
        }
        onClose()
      }}
      onCancel={(event) => {
        if (!dismissible) event.preventDefault()
      }}
      // A click on the dim area outside the box lands on the <dialog> itself.
      onClick={(event) => {
        if (dismissible && event.target === event.currentTarget) onClose()
      }}
      className={
        variant === 'fullscreen'
          ? 'm-0 h-dvh max-h-none w-dvw max-w-none animate-fade rounded-none border-0 bg-slate-900 p-0 text-white backdrop:bg-slate-900'
          : cn(
              // The pop shadow draws the hairline edge too, so there is no border. The box clips what is inside it to its corners.
              'm-auto w-[calc(100%-2rem)] animate-pop overflow-hidden rounded-2xl bg-surface p-0 shadow-pop backdrop:animate-fade backdrop:bg-slate-900/55 backdrop:backdrop-blur-sm',
              variant === 'wide'
                ? 'max-w-4xl max-sm:m-0 max-sm:h-dvh max-sm:max-h-none max-sm:w-dvw max-sm:max-w-none max-sm:rounded-none max-sm:shadow-none'
                : 'max-w-md',
            )
      }
    >
      {open && variant === 'fullscreen' && children}
      {open && variant !== 'fullscreen' && (
        <div className={cn('flex flex-col', variant === 'wide' ? 'h-[85dvh] max-sm:h-dvh' : 'max-h-[85dvh]')}>
          {/* A dialog that scrolls inside (the gallery) keeps a line under its title, so the content slides beneath it. */}
          <div className={cn('flex items-center justify-between px-5 pb-3 pt-5', variant === 'wide' && 'border-b border-line pb-4')}>
            <h2 className="text-lg font-semibold tracking-tight text-fg">{title}</h2>
            {dismissible && (
              <Button variant="ghost" size="icon" aria-label="Close" onClick={onClose}>
                <X className="h-5 w-5" />
              </Button>
            )}
          </div>
          {children}
        </div>
      )}
    </dialog>
  )
}
