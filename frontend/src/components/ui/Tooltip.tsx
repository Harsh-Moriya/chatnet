import { type ReactElement, cloneElement, useEffect, useId, useLayoutEffect, useRef, useState } from 'react'
import { cn } from '../../lib/cn'
import { shiftIntoView } from './tooltipPosition'

const SHOW_DELAY_MS = 400

interface TooltipProps {
  label: string
  // Below the control (the default), or above it for one at the bottom of the screen.
  side?: 'top' | 'bottom'
  // One control, which is given the tooltip as its description when the words say more than its own label does.
  children: ReactElement<{ 'aria-label'?: string; 'aria-describedby'?: string }>
}

// The words for an icon button, shown when the mouse rests on it or the keyboard reaches it. It is not shown to a touch (a tap would leave it
// stuck on the screen), it goes away on Escape, on leaving, and when the control is pressed, and it moves sideways to stay inside the window.
export function Tooltip({ label, side = 'bottom', children }: TooltipProps) {
  const id = useId()
  const [open, setOpen] = useState(false)
  const timer = useRef<number | undefined>(undefined)
  const bubble = useRef<HTMLSpanElement>(null)

  const show = (delay: number) => {
    window.clearTimeout(timer.current)
    timer.current = window.setTimeout(() => setOpen(true), delay)
  }
  const hide = () => {
    window.clearTimeout(timer.current)
    setOpen(false)
  }

  useEffect(() => () => window.clearTimeout(timer.current), [])

  // Measured once it is showing, before it is drawn, and moved by a margin so a control at the edge of the window keeps its words whole.
  useLayoutEffect(() => {
    const element = bubble.current
    if (!element) return
    element.style.marginLeft = ''
    if (!open) return
    const shift = shiftIntoView(element.getBoundingClientRect(), window.innerWidth)
    if (shift !== 0) element.style.marginLeft = `${shift}px`
  }, [open])

  const describes = children.props['aria-label'] !== label

  return (
    <span
      className="relative inline-flex"
      onPointerEnter={(event) => {
        if (event.pointerType === 'mouse') show(SHOW_DELAY_MS)
      }}
      onPointerLeave={hide}
      onPointerDown={hide}
      onFocus={(event) => {
        if (event.target.matches(':focus-visible')) show(0)
      }}
      onBlur={hide}
      onKeyDown={(event) => {
        if (event.key === 'Escape') hide()
      }}
    >
      {cloneElement(children, { 'aria-describedby': describes ? id : undefined })}
      <span
        ref={bubble}
        id={id}
        role="tooltip"
        className={cn(
          'pointer-events-none absolute left-1/2 z-30 w-max max-w-64 animate-fade -translate-x-1/2 rounded-lg bg-fg px-2.5 py-1 text-center text-xs font-medium text-page shadow-pop',
          side === 'bottom' ? 'top-full mt-1.5' : 'bottom-full mb-1.5',
          // Not drawn while closed, so a tooltip that would hang over the edge of the window cannot make the page scroll sideways.
          // A control can still be described by it: a description may come from an element that is not displayed.
          open ? 'block' : 'hidden',
        )}
      >
        {label}
      </span>
    </span>
  )
}
