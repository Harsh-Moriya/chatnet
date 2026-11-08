import { AlertCircle } from 'lucide-react'

// The red banner of these screens: the same one the profile uses, for a step that failed.
export function Problem({ message }: { message: string | null }) {
  if (message === null) return null

  return (
    <div role="alert" className="flex items-start gap-2 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-xs text-red-600">
      <AlertCircle className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden="true" />
      <span>{message}</span>
    </div>
  )
}
