import { Mic, MicOff, Minimize2, PhoneOff, ShieldCheck, Video, VideoOff } from 'lucide-react'
import { Avatar } from '../../../components/ui/Avatar'
import { Button } from '../../../components/ui/Button'
import { Dialog } from '../../../components/ui/Dialog'
import { cn } from '../../../lib/cn'
import { useNow } from '../../../lib/useNow'
import type { CallActions } from '../../chat/hooks/chatConnectionContext'
import { useVerified } from '../../encryption/hooks/useVerified'
import { useCallStore } from '../callStore'
import { callStatus } from '../callTexts'
import { useStream } from '../useStream'
import { DeviceMenu } from './DeviceMenu'

interface CallViewProps {
  open: boolean
  calls: CallActions
}

// The call from the first tap on "call" to the moment it has ended: who it is with, what is happening, and the buttons. Full screen, and it
// cannot be dismissed: hanging up is the red button, and the call can be put away as a bar with the minimise button (not with Escape).
export function CallView({ open, calls }: CallViewProps) {
  const phase = useCallStore((state) => state.phase)
  const peerId = useCallStore((state) => state.peerId)
  const peerName = useCallStore((state) => state.peerName) ?? ''
  const media = useCallStore((state) => state.media)
  const answeredAt = useCallStore((state) => state.answeredAt)
  const sendingVideo = useCallStore((state) => state.sendingVideo)
  const muted = useCallStore((state) => state.muted)
  const cameraOff = useCallStore((state) => state.cameraOff)
  const reconnecting = useCallStore((state) => state.reconnecting)
  const notice = useCallStore((state) => state.notice)
  const localStream = useCallStore((state) => state.localStream)
  const remoteStream = useCallStore((state) => state.remoteStream)
  const setMinimised = useCallStore((state) => state.setMinimised)
  const verified = useVerified(peerId ?? null)
  const now = useNow(1000)

  const remoteRef = useStream<HTMLVideoElement>(remoteStream)
  const localRef = useStream<HTMLVideoElement>(localStream)

  const video = media === 'Video'
  const running = phase === 'active' || phase === 'connecting'
  const ended = phase === 'ended'
  const showRemoteVideo = video && phase === 'active'
  const elapsed = answeredAt === null ? 0 : Math.max(0, Math.floor((now.getTime() - answeredAt) / 1000))

  const status = callStatus({ phase, reconnecting, elapsedSeconds: elapsed })

  return (
    <Dialog open={open} onClose={() => undefined} title="Call" variant="fullscreen" dismissible={false}>
      <div className="relative flex h-full flex-col">
        {/* The picture only: the sound is CallSound's, which also carries on when this screen is put away. */}
        <video
          ref={remoteRef}
          autoPlay
          playsInline
          muted
          aria-label={`${peerName}'s video`}
          className={cn('absolute inset-0 h-full w-full bg-slate-900 object-cover', !showRemoteVideo && 'invisible')}
        />

        <div className="relative flex flex-1 flex-col items-center justify-center gap-4 px-6 text-center">
          {!showRemoteVideo && <Avatar name={peerName} size="lg" />}
          <div className={cn(showRemoteVideo && 'absolute left-4 top-4 flex flex-col items-start rounded-lg bg-slate-900/60 px-3 py-2 text-left')}>
            <p className="flex items-center gap-1.5 text-lg font-semibold">
              {peerName}
              {verified && <ShieldCheck className="h-5 w-5 text-sky-400" aria-label="Verified" />}
            </p>
            <p className="text-sm text-slate-300" role="status">
              {status}
            </p>
          </div>
          {notice !== null && (
            <p className="max-w-sm rounded-lg bg-slate-800 px-4 py-3 text-sm text-slate-100" role="alert">
              {notice}
            </p>
          )}
        </div>

        {sendingVideo && localStream !== null && (
          <video
            ref={localRef}
            autoPlay
            playsInline
            muted
            aria-label="Your video"
            className={cn(
              'absolute bottom-28 right-4 h-32 w-24 -scale-x-100 rounded-lg border border-slate-600 bg-slate-800 object-cover shadow-lg sm:h-40 sm:w-32',
              cameraOff && 'invisible',
            )}
          />
        )}

        {!ended && (
          <div className="relative flex items-center justify-center gap-3 px-4 pb-[max(1.5rem,env(safe-area-inset-bottom))] pt-4">
            {running && (
              <Button
                variant="ghost"
                size="icon"
                className="h-12! w-12! rounded-full! bg-slate-700! text-white! hover:bg-slate-600!"
                aria-label={muted ? 'Unmute microphone' : 'Mute microphone'}
                aria-pressed={muted}
                onClick={() => calls.setMuted(!muted)}
              >
                {muted ? <MicOff className="h-5 w-5" /> : <Mic className="h-5 w-5" />}
              </Button>
            )}
            {running && sendingVideo && (
              <Button
                variant="ghost"
                size="icon"
                className="h-12! w-12! rounded-full! bg-slate-700! text-white! hover:bg-slate-600!"
                aria-label={cameraOff ? 'Turn camera on' : 'Turn camera off'}
                aria-pressed={cameraOff}
                onClick={() => calls.setCameraOff(!cameraOff)}
              >
                {cameraOff ? <VideoOff className="h-5 w-5" /> : <Video className="h-5 w-5" />}
              </Button>
            )}
            {running && <DeviceMenu video={sendingVideo} onSwitch={calls.switchDevice} />}
            <Button variant="danger" className="h-12! rounded-full! px-6" onClick={() => calls.end()}>
              <PhoneOff className="h-5 w-5" aria-hidden="true" />
              {phase === 'starting' || phase === 'outgoing' ? 'Cancel' : 'Hang up'}
            </Button>
          </div>
        )}

        {!ended && (
          <Button
            variant="ghost"
            size="icon"
            className="absolute right-4 top-[max(1rem,env(safe-area-inset-top))] rounded-full! bg-slate-900/60! text-white! hover:bg-slate-700!"
            aria-label="Minimise call"
            onClick={() => setMinimised(true)}
          >
            <Minimize2 className="h-5 w-5" />
          </Button>
        )}
      </div>
    </Dialog>
  )
}
