import { type TextareaHTMLAttributes, forwardRef } from 'react'
import { cn } from '../../lib/cn'

interface TextareaProps extends TextareaHTMLAttributes<HTMLTextAreaElement> {
  error?: string
  // No border, ring or fill of its own, for a box that sits inside a container that draws them (the message bar).
  bare?: boolean
}

// The multi-line counterpart of Input, with the same look. It does not resize by itself; the
// caller sets the height (the message composer grows it as the person types).
export const Textarea = forwardRef<HTMLTextAreaElement, TextareaProps>(
  function Textarea({ error, bare = false, className, ...props }, ref) {
    return (
      <div className="w-full">
        <textarea
          ref={ref}
          className={cn(
            bare
              ? 'flex w-full resize-none bg-transparent px-2 py-2 text-sm text-fg placeholder:text-fg-faint focus:outline-none disabled:cursor-not-allowed'
              : cn(
                  'flex w-full resize-none rounded-lg border bg-surface px-3 py-2 text-sm text-fg',
                  'placeholder:text-fg-faint transition-colors',
                  'focus:outline-none focus:ring-2',
                  'disabled:cursor-not-allowed disabled:opacity-50',
                  error
                    ? 'border-red-400 focus:border-red-400 focus:ring-red-500/20'
                    : 'border-line focus:border-blue-500 focus:ring-blue-500/20',
                ),
            className,
          )}
          {...props}
        />
        {error && <p className="mt-1.5 text-xs text-danger-fg">{error}</p>}
      </div>
    )
  },
)

Textarea.displayName = 'Textarea'
