import type { ComponentProps } from 'react'
import { Button } from '../../../components/ui/Button'

// The icon button of the viewer, whose background is dark: the ghost button with light text.
export function ViewerButton({ className = '', ...props }: ComponentProps<typeof Button>) {
  return (
    <Button
      variant="ghost"
      size="icon"
      className={`text-white hover:bg-white/15 active:bg-white/25 focus-visible:ring-offset-slate-900 ${className}`}
      {...props}
    />
  )
}
