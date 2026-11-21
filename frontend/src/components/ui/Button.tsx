import { type ButtonHTMLAttributes, type ReactNode } from 'react'
import { Spinner } from './Spinner'

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: 'primary' | 'ghost' | 'secondary' | 'success' | 'danger' | 'link'
  // `icon-sm` and `icon-xs` are the small crosses and eyes inside a field or a chip. The `link` variant is a line of text that acts like a
  // button and has no size of its own.
  size?: 'md' | 'sm' | 'icon' | 'icon-sm' | 'icon-xs'
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
    'inline-flex items-center justify-center gap-2 font-medium rounded-control transition active:scale-[0.97]',
    'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:ring-offset-2 focus-visible:ring-offset-surface',
    'disabled:opacity-50 disabled:pointer-events-none',
  ].join(' ')

  const variants = {
    primary: 'bg-blue-600 text-white hover:bg-blue-700 active:bg-blue-800',
    success: 'bg-emerald-600 text-white hover:bg-emerald-700 active:bg-emerald-800',
    danger: 'bg-red-600 text-white hover:bg-red-700 active:bg-red-800',
    ghost: 'bg-transparent text-fg-soft hover:bg-subtle active:bg-strong',
    secondary: 'border border-line bg-surface text-fg hover:bg-subtle active:bg-strong',
    link: 'bg-transparent text-accent-fg hover:underline',
  }

  const sizes = {
    md: 'h-10 px-4 text-sm',
    sm: 'h-8 px-3 text-xs',
    icon: 'h-9 w-9 shrink-0 p-0',
    'icon-sm': 'h-7 w-7 shrink-0 p-0',
    'icon-xs': 'h-5 w-5 shrink-0 rounded-full! p-0',
  }

  return (
    <button
      className={[base, variants[variant], variant === 'link' ? 'h-auto p-0' : sizes[size], fullWidth ? 'w-full' : '', className]
        .filter(Boolean)
        .join(' ')}
      disabled={disabled || isLoading}
      {...rest}
    >
      {isLoading && <Spinner />}
      {children}
    </button>
  )
}
