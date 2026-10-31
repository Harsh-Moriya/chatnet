import { type ReactNode, useEffect, useRef, useState } from 'react'
import { MoreHorizontal } from 'lucide-react'
import { cn } from '../../lib/cn'

export interface MenuItem {
  label: string
  icon: ReactNode
  onSelect: () => void
}

interface MenuProps {
  // What the trigger is called for someone who cannot see it.
  label: string
  items: MenuItem[]
  // Which edge of the trigger the list lines up with.
  align?: 'start' | 'end'
  className?: string
}

// A button that opens a short list of actions. It closes on Escape (returning focus to the button), on a click
// outside, on Tab and after an item is chosen; the arrow keys, Home and End move between items. The list is
// positioned against the trigger, so the parent has to leave it room and not clip it.
export function Menu({ label, items, align = 'end', className }: MenuProps) {
  const [open, setOpen] = useState(false)
  const root = useRef<HTMLDivElement>(null)
  const trigger = useRef<HTMLButtonElement>(null)
  const list = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return

    const choices = list.current?.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')
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
    const choices = [...(list.current?.querySelectorAll<HTMLButtonElement>('[role="menuitem"]') ?? [])]
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
          'flex h-7 w-7 items-center justify-center rounded-lg text-slate-400 transition-colors',
          'hover:bg-slate-100 hover:text-slate-700',
          'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500',
          open && 'bg-slate-100 text-slate-700',
        )}
      >
        <MoreHorizontal className="h-4 w-4" aria-hidden="true" />
      </button>

      {open && (
        <div
          ref={list}
          role="menu"
          aria-label={label}
          className={cn(
            'absolute top-full z-20 mt-1 min-w-36 rounded-lg border border-line bg-white py-1 shadow-lg',
            align === 'end' ? 'right-0' : 'left-0',
          )}
        >
          {items.map((item) => (
            <button
              key={item.label}
              type="button"
              role="menuitem"
              onClick={() => {
                close(false)
                item.onSelect()
              }}
              className={cn(
                'flex w-full items-center gap-2 px-3 py-2 text-left text-sm text-slate-700 transition-colors',
                'hover:bg-slate-100 focus-visible:bg-slate-100 focus-visible:outline-none',
              )}
            >
              <span className="text-slate-500" aria-hidden="true">
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
