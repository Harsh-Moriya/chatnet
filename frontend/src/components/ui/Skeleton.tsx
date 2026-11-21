import { cn } from '../../lib/cn'

// A grey block in the shape of what is on its way, shown instead of a spinner while a whole list or page loads. Hidden from a screen
// reader: the place that shows it says that it is loading.
export function Skeleton({ className }: { className?: string }) {
  return <div aria-hidden="true" className={cn('animate-pulse bg-strong', className)} />
}
