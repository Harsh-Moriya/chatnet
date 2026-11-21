import { type ReactNode } from 'react'
import { AlertCircle, Info, TriangleAlert } from 'lucide-react'
import { cn } from '../../lib/cn'

interface AlertProps {
  kind?: 'danger' | 'warning' | 'info'
  // `sm` for a box inside a dense panel (the encryption settings).
  size?: 'md' | 'sm'
  // Left to the caller: some of these boxes announce themselves when they appear and some sit in a form that already does.
  role?: 'alert' | 'status' | 'note'
  // A button that answers the message (Try again), drawn at the end.
  action?: ReactNode
  className?: string
  children: ReactNode
}

const TONES = {
  danger: { box: 'border-danger-line/70 bg-danger-soft text-danger-fg', Icon: AlertCircle },
  warning: { box: 'border-warn-line/70 bg-warn-soft text-warn-fg', Icon: TriangleAlert },
  info: { box: 'border-accent-soft-strong/70 bg-accent-soft text-accent-fg', Icon: Info },
}

// A message about what went wrong, or what to know, in a box. One look for every place that used to write its own.
export function Alert({ kind = 'danger', size = 'md', role, action, className, children }: AlertProps) {
  const { box, Icon } = TONES[kind]

  return (
    <div
      role={role}
      className={cn(
        'flex gap-2 rounded-xl border px-3.5 py-2.5',
        action ? 'items-center' : 'items-start',
        size === 'sm' ? 'text-xs' : 'text-sm',
        box,
        className,
      )}
    >
      <Icon className={cn('shrink-0', size === 'sm' ? 'h-3.5 w-3.5' : 'h-4 w-4', !action && 'mt-0.5')} aria-hidden="true" />
      <div className="min-w-0 flex-1">{children}</div>
      {action}
    </div>
  )
}
