import { cn } from '../../lib/cn'

// A grey block in the shape of what is on its way, shown instead of a spinner while a whole list or page loads. Hidden from a screen
// reader: the place that shows it says that it is loading.
export function Skeleton({ className }: { className?: string }) {
  return <div aria-hidden="true" className={cn('animate-pulse bg-strong', className)} />
}

// Rows of a picture and two lines of text, the shape of a conversation or a notification.
export function RowsSkeleton({ rows = 6, label, className }: { rows?: number; label: string; className?: string }) {
  return (
    <div role="status" className={cn('space-y-1 p-4', className)}>
      <span className="sr-only">{label}</span>
      {Array.from({ length: rows }, (_, index) => (
        <div key={index} className="flex items-center gap-3 py-2">
          <Skeleton className="h-11 w-11 shrink-0 rounded-full" />
          <div className="flex-1 space-y-2">
            <Skeleton className="h-3 w-1/2 rounded" />
            <Skeleton className="h-3 w-3/4 rounded opacity-60" />
          </div>
        </div>
      ))}
    </div>
  )
}
