import { type ButtonHTMLAttributes, type ReactNode } from 'react'

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: 'primary' | 'ghost'
  size?: 'md' | 'sm' | 'icon'
  isLoading?: boolean
  fullWidth?: boolean
  children: ReactNode
}

export function Button({
  variant = 'primary',
  size = 'md',
  isLoading = false,
  fullWidth = false,
  children,
  className = '',
  disabled,
  ...rest
}: ButtonProps) {
  const base = [
    'inline-flex items-center justify-center gap-2 font-medium rounded-lg transition-colors',
    'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2',
    'disabled:opacity-50 disabled:pointer-events-none',
  ].join(' ')

  const variants = {
    primary: 'bg-blue-600 text-white hover:bg-blue-700 active:bg-blue-800',
    ghost: 'bg-transparent text-slate-700 hover:bg-slate-100 active:bg-slate-200',
  }

  const sizes = {
    md: 'h-10 px-4 text-sm',
    sm: 'h-8 px-3 text-xs',
    icon: 'h-9 w-9 shrink-0 p-0',
  }

  return (
    <button
      className={[base, variants[variant], sizes[size], fullWidth ? 'w-full' : '', className]
        .filter(Boolean)
        .join(' ')}
      disabled={disabled || isLoading}
      {...rest}
    >
      {isLoading && (
        <svg
          className="h-4 w-4 animate-spin"
          fill="none"
          viewBox="0 0 24 24"
          aria-hidden="true"
        >
          <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
          <path
            className="opacity-75"
            fill="currentColor"
            d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z"
          />
        </svg>
      )}
      {children}
    </button>
  )
}
