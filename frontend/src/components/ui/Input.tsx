import { type InputHTMLAttributes, type ReactNode, forwardRef } from 'react'

interface InputProps extends InputHTMLAttributes<HTMLInputElement> {
  error?: string
  leftIcon?: ReactNode
  rightElement?: ReactNode
}

// forwardRef lets parent components attach a ref to the underlying <input>
// element; needed by form libraries and programmatic focus.
export const Input = forwardRef<HTMLInputElement, InputProps>(
  function Input({ error, leftIcon, rightElement, className = '', ...props }, ref) {
    const baseClasses = [
      'flex h-10 w-full rounded-lg border bg-white px-3 py-2 text-sm text-slate-900',
      'placeholder:text-slate-400 transition-colors',
      'focus:outline-none focus:ring-2',
      'disabled:cursor-not-allowed disabled:opacity-50',
      error
        ? 'border-red-400 focus:border-red-400 focus:ring-red-500/20'
        : 'border-line focus:border-blue-500 focus:ring-blue-500/20',
      leftIcon ? 'pl-10' : '',
      rightElement ? 'pr-10' : '',
      className,
    ]
      .filter(Boolean)
      .join(' ')

    return (
      <div className="relative w-full">
        {leftIcon && (
          <div className="pointer-events-none absolute inset-y-0 left-0 flex items-center pl-3 text-slate-400">
            {leftIcon}
          </div>
        )}
        <input ref={ref} className={baseClasses} {...props} />
        {rightElement && (
          <div className="absolute inset-y-0 right-0 flex items-center pr-3">
            {rightElement}
          </div>
        )}
        {error && <p className="mt-1.5 text-xs text-red-500">{error}</p>}
      </div>
    )
  }
)

Input.displayName = 'Input'
