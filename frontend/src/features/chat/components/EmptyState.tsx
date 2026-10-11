import { MessagesSquare } from 'lucide-react'

export function EmptyState() {
  return (
    <div className="flex flex-1 flex-col items-center justify-center gap-3 p-8 text-center">
      <div className="flex h-14 w-14 items-center justify-center rounded-full bg-blue-100 text-blue-600">
        <MessagesSquare className="h-7 w-7" />
      </div>
      <h2 className="text-base font-semibold text-slate-900">Select a conversation</h2>
      <p className="max-w-xs text-sm text-slate-500">
        Pick one from the list, or start a new one with the pen icon.
      </p>
    </div>
  )
}
