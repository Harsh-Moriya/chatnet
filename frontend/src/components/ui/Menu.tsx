import { type ReactNode, useEffect, useRef, useState } from 'react'
import { EllipsisVertical, MoreHorizontal } from 'lucide-react'
import { cn } from '../../lib/cn'

export interface MenuItem {
  label: string
  icon: ReactNode
  onSelect: () => void
  // Shown, but not chosen and skipped by the arrow keys (a request is running).
  disabled?: boolean
}

interface MenuProps {
  // What the trigger is called for someone who cannot see it.
  label: string
  items: MenuItem[]
  // Which edge of the trigger the list lines up with.
  align?: 'start' | 'end'
  // `inline` is the small trigger at the end of a row (a message, a member); `toolbar` is a button like the icon buttons of a header.
  variant?: 'inline' | 'toolbar'
  className?: string
}

// The items that can be chosen: a disabled one is passed over.
const CHOICES = '[role="menuitem"]:not(:disabled)'

// A button that opens a short list of actions. It closes on Escape (returning focus to the button), on a click
// outside, on Tab and after an item is chosen (when focus goes back to the button before the item runs, so a dialog the item opens gives focus
// back to the button when it closes); the arrow keys, Home and End move between items. The list is positioned against the trigger, so the
// parent has to leave it room and not clip it.
export function Menu({ label, items, align = 'end', variant = 'inline', className }: MenuProps) {
  const [open, setOpen] = useState(false)
  const root = useRef<HTMLDivElement>(null)
  const trigger = useRef<HTMLButtonElement>(null)
  const list = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return

    const choices = list.current?.querySelectorAll<HTMLButtonElement>(CHOICES)
    choices?.[0]?.focus()

    const onPointerDown = (event: PointerEvent) => {
      if (!root.current?.contains(event.target as Node)) setOpen(false)
    }
    document.addEventListener('pointerdown', onPointerDown)
    return () => document.removeEventListener('pointerdown', onPointerDown)
  }, [open])

  function close(returnFocus: boolean) {
    setOpen(false)
    if (returnFocus) trigger.current?.focus()
  }

  function onKeyDown(event: React.KeyboardEvent) {
    const choices = [...(list.current?.querySelectorAll<HTMLButtonElement>(CHOICES) ?? [])]
    const current = choices.indexOf(document.activeElement as HTMLButtonElement)

    switch (event.key) {
      case 'Escape':
        event.preventDefault()
        close(true)
        break
      case 'Tab':
        setOpen(false)
        break
      case 'ArrowDown':
        event.preventDefault()
        choices[(current + 1) % choices.length]?.focus()
        break
      case 'ArrowUp':
        event.preventDefault()
        choices[(current - 1 + choices.length) % choices.length]?.focus()
        break
      case 'Home':
        event.preventDefault()
        choices[0]?.focus()
        break
      case 'End':
        event.preventDefault()
        choices[choices.length - 1]?.focus()
        break
    }
  }

  return (
    <div ref={root} className={cn('relative', className)} onKeyDown={open ? onKeyDown : undefined}>
      <button
        ref={trigger}
        type="button"
        aria-label={label}
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={() => setOpen((value) => !value)}
        className={cn(
          'flex items-center justify-center rounded-control transition-colors',
          'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500',
          variant === 'toolbar'
            ? 'h-9 w-9 shrink-0 text-fg-soft hover:bg-subtle active:bg-strong'
            : 'h-7 w-7 text-fg-faint hover:bg-subtle hover:text-fg-soft',
          open && 'bg-subtle text-fg-soft',
        )}
      >
        {variant === 'toolbar' ? (
          <EllipsisVertical className="h-5 w-5" aria-hidden="true" />
        ) : (
          <MoreHorizontal className="h-4 w-4" aria-hidden="true" />
        )}
      </button>

      {open && (
        <div
          ref={list}
          role="menu"
          aria-label={label}
          className={cn(
            'absolute top-full z-20 mt-1.5 animate-pop rounded-xl bg-surface p-1.5 shadow-pop',
            variant === 'toolbar' ? 'min-w-56' : 'min-w-36',
            align === 'end' ? 'right-0 origin-top-right' : 'left-0 origin-top-left',
          )}
        >
          {items.map((item) => (
            <button
              key={item.label}
              type="button"
              role="menuitem"
              disabled={item.disabled}
              onClick={() => {
                close(true)
                item.onSelect()
              }}
              className={cn(
                'flex w-full items-center gap-2 rounded-lg px-3 py-2 text-left text-sm text-fg-soft transition-colors pointer-coarse:py-3',
                'hover:bg-subtle focus-visible:bg-subtle focus-visible:outline-none',
                'disabled:pointer-events-none disabled:opacity-50',
              )}
            >
              <span className="text-fg-muted" aria-hidden="true">
                {item.icon}
              </span>
              {item.label}
            </button>
          ))}
        </div>
      )}
    </div>
  )
}
