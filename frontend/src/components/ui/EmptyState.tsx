import { type ReactNode } from 'react'
import { type LucideIcon } from 'lucide-react'
import { cn } from '../../lib/cn'

interface EmptyStateProps {
  icon: LucideIcon
  // Names the place that is empty, as a plain statement.
  title: string
  // One line that says what will be here, or what to do.
  children?: ReactNode
  // What the person can do about it (a button).
  action?: ReactNode
  className?: string
}

// What a list, a panel or a page shows when there is nothing in it: a quiet icon, a title and a line. Not for an error, which is an Alert.
export function EmptyState({ icon: Icon, title, children, action, className }: EmptyStateProps) {
  return (
    <div className={cn('flex flex-col items-center gap-1 p-8 text-center', className)}>
      <span className="mb-2 flex h-12 w-12 items-center justify-center rounded-full bg-subtle text-fg-muted">
        <Icon className="h-6 w-6" aria-hidden="true" />
      </span>
      <p className="max-w-xs break-words text-sm font-medium text-fg">{title}</p>
      {children && <p className="max-w-xs text-sm text-fg-muted">{children}</p>}
      {action && <div className="mt-2">{action}</div>}
    </div>
  )
}
