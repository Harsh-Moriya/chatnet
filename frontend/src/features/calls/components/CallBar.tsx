import { Maximize2, Mic, MicOff, PhoneOff, ShieldCheck } from 'lucide-react'
import { Avatar } from '../../../components/ui/Avatar'
import { Button } from '../../../components/ui/Button'
import { useNow } from '../../../lib/useNow'
import type { CallActions } from '../../chat/hooks/chatConnectionContext'
import { useVerified } from '../../encryption/hooks/useVerified'
import { useCallStore } from '../callStore'
import { callStatus } from '../callTexts'

interface CallBarProps {
  calls: CallActions
}

// The call put away: a row above the chat with who it is with, how it is going, mute and hang up, so the conversation can be read and
// answered while the call goes on. The name and status bring the full screen back.
export function CallBar({ calls }: CallBarProps) {
  const phase = useCallStore((state) => state.phase)
  const peerId = useCallStore((state) => state.peerId)
  const peerName = useCallStore((state) => state.peerName) ?? ''
  const answeredAt = useCallStore((state) => state.answeredAt)
  const muted = useCallStore((state) => state.muted)
  const reconnecting = useCallStore((state) => state.reconnecting)
  const notice = useCallStore((state) => state.notice)
  const setMinimised = useCallStore((state) => state.setMinimised)
  const verified = useVerified(peerId ?? null)
  const now = useNow(1000)

  const ended = phase === 'ended'
  const running = phase === 'active' || phase === 'connecting'
  const elapsedSeconds = answeredAt === null ? 0 : Math.max(0, Math.floor((now.getTime() - answeredAt) / 1000))
  // A call that ended says why (declined, dropped) beside "Call ended", and a notice during the call (a lost microphone) is not lost
  // because the screen that shows it is away.
  const line = [callStatus({ phase, reconnecting, elapsedSeconds }), notice].filter(Boolean).join(' · ')

  const who = (
    <>
      <Avatar name={peerName} size="sm" />
      <span className="min-w-0 flex-1">
        <span className="flex items-center gap-1.5 text-sm font-semibold">
          <span className="truncate">{peerName}</span>
          {verified && <ShieldCheck className="h-4 w-4 shrink-0 text-sky-400" aria-label="Verified" />}
        </span>
        <span className="block truncate text-xs text-slate-300" role="status" title={line}>
          {line}
        </span>
      </span>
    </>
  )

  return (
    <div className="flex shrink-0 items-center gap-2 border-b border-slate-700 bg-slate-900 px-3 py-2 text-white">
      {ended ? (
        <div className="flex min-w-0 flex-1 items-center gap-3 px-1 py-1">{who}</div>
      ) : (
        <Button
          variant="ghost"
          className="h-auto! min-w-0 flex-1 justify-start! gap-3! px-1! py-1! text-left text-white! hover:bg-slate-800! active:bg-slate-700!"
          aria-label={`Return to the call with ${peerName}`}
          onClick={() => setMinimised(false)}
        >
          {who}
          <Maximize2 className="h-4 w-4 shrink-0 text-slate-300" aria-hidden="true" />
        </Button>
      )}

      {running && (
        <Button
          variant="ghost"
          size="icon"
          className="rounded-full! bg-slate-700! text-white! hover:bg-slate-600!"
          aria-label={muted ? 'Unmute microphone' : 'Mute microphone'}
          aria-pressed={muted}
          onClick={() => calls.setMuted(!muted)}
        >
          {muted ? <MicOff className="h-5 w-5" /> : <Mic className="h-5 w-5" />}
        </Button>
      )}
      {!ended && (
        <Button variant="danger" className="rounded-full! px-3 sm:px-4" onClick={() => calls.end()}>
          <PhoneOff className="h-5 w-5" aria-hidden="true" />
          <span className="max-sm:sr-only">{phase === 'starting' || phase === 'outgoing' ? 'Cancel' : 'Hang up'}</span>
        </Button>
      )}
    </div>
  )
}
