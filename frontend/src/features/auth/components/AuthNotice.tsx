import type { ReactNode } from 'react'
import { CheckCircle2 } from 'lucide-react'

// A message that is neither an error nor a form: something that happened and was fine.
export function AuthNotice({ children }: { children: ReactNode }) {
  return (
    <div className="flex items-start gap-2.5 rounded-lg border border-blue-200 bg-blue-50 px-3.5 py-3" role="status">
      <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-blue-600" />
      <p className="text-sm text-blue-900">{children}</p>
    </div>
  )
}
