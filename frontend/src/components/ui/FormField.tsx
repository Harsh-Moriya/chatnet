import { type InputHTMLAttributes, type ReactNode } from 'react'
import { Input } from './Input'

interface FormFieldProps extends InputHTMLAttributes<HTMLInputElement> {
  label: string
  error?: string
  rightElement?: ReactNode
}

// Composes a label + Input + inline error into a single unit.
// All standard input props (type, value, onChange, placeholder, etc.) pass through.
export function FormField({ label, error, id, rightElement, ...inputProps }: FormFieldProps) {
  const fieldId = id ?? label.toLowerCase().replace(/\s+/g, '-')

  return (
    <div className="space-y-1.5">
      <label htmlFor={fieldId} className="block text-sm font-medium text-fg-soft">
        {label}
      </label>
      <Input id={fieldId} error={error} rightElement={rightElement} {...inputProps} />
    </div>
  )
}
