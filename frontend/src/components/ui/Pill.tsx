import { type HTMLAttributes } from 'react'
import { cn } from '../../lib/cn'

interface PillProps extends HTMLAttributes<HTMLSpanElement> {
  // `neutral` and `accent` are quiet tags; `solid` and `quiet` carry a count (the second for a muted conversation, which still counts
  // but does not ask for attention); `chip` is the line in the middle of a thread (a day, a call).
  tone?: 'neutral' | 'accent' | 'solid' | 'quiet' | 'chip'
  // `count` is a round badge for a number.
  size?: 'sm' | 'md' | 'count'
}

const TONES = {
  neutral: 'bg-subtle text-fg-muted',
  accent: 'bg-accent-soft text-accent-fg',
  solid: 'bg-blue-600 text-white',
  quiet: 'bg-slate-500 text-white',
  chip: 'bg-strong/70 text-fg-muted',
}

const SIZES = {
  sm: 'px-2 py-0.5 text-[11px] font-medium',
  md: 'px-3 py-1 text-xs font-medium',
  count: 'h-5 min-w-5 justify-center px-1.5 text-[11px] font-semibold',
}

// A small rounded label: a tag, a count, or a line in the thread. One look for every place that used to write its own.
export function Pill({ tone = 'neutral', size = 'sm', className, ...rest }: PillProps) {
  return <span className={cn('inline-flex shrink-0 items-center gap-1.5 rounded-full', TONES[tone], SIZES[size], className)} {...rest} />
}
