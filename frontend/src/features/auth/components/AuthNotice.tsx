import type { ReactNode } from 'react'
import { CheckCircle2 } from 'lucide-react'

// A message that is neither an error nor a form: something that happened and was fine.
export function AuthNotice({ children }: { children: ReactNode }) {
  return (
    <div className="flex items-start gap-2.5 rounded-lg border border-accent-soft-strong bg-accent-soft px-3.5 py-3" role="status">
      <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-accent-fg" />
      <p className="text-sm text-fg">{children}</p>
    </div>
  )
}
