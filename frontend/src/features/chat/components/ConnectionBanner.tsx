import { AlertCircle, LoaderCircle } from 'lucide-react'
import { useChatStore } from '../store/chatStore'

// Tells the user when messages are not live. REST still works while the connection is down, so this
// is a status, not a block: sending and reading carry on.
export function ConnectionBanner() {
  const status = useChatStore((state) => state.connection)

  if (status === 'connected') return null

  if (status === 'disconnected') {
    return (
      <div role="status" className="flex items-center gap-2 border-b border-red-200 bg-red-50 px-4 py-2 text-xs text-red-600">
        <AlertCircle className="h-4 w-4 shrink-0" />
        <span>Can&apos;t reach the server. Trying again.</span>
      </div>
    )
  }

  return (
    <div role="status" className="flex items-center gap-2 border-b border-slate-200 bg-slate-50 px-4 py-2 text-xs text-slate-500">
      <LoaderCircle className="h-4 w-4 shrink-0 animate-spin" />
      <span>{status === 'connecting' ? 'Connecting…' : 'Connection lost. Reconnecting…'}</span>
    </div>
  )
}
