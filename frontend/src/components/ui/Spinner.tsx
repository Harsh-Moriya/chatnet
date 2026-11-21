import { LoaderCircle } from 'lucide-react'
import { cn } from '../../lib/cn'

interface SpinnerProps {
  size?: 'xs' | 'sm' | 'md' | 'lg'
  // Said to a screen reader when no text beside the spinner says it; the spinner is then a status. Left out, it is only decoration.
  label?: string
  className?: string
}

const SIZES = { xs: 'h-3.5 w-3.5', sm: 'h-4 w-4', md: 'h-5 w-5', lg: 'h-8 w-8' }

// The turning mark for something in progress, such as a request or a page that is being fetched.
export function Spinner({ size = 'sm', label, className }: SpinnerProps) {
  return (
    <LoaderCircle
      className={cn('shrink-0 animate-spin', SIZES[size], className)}
      role={label ? 'status' : undefined}
      aria-label={label}
      aria-hidden={label ? undefined : true}
    />
  )
}
