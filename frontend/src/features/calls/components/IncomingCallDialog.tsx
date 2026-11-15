import { Phone, PhoneOff, Video } from 'lucide-react'
import { Avatar } from '../../../components/ui/Avatar'
import { Button } from '../../../components/ui/Button'
import { Dialog } from '../../../components/ui/Dialog'
import type { CallActions } from '../../chat/hooks/chatConnectionContext'
import { useCallStore } from '../callStore'

interface IncomingCallDialogProps {
  open: boolean
  calls: CallActions
}

// A call is ringing here. It is a modal on purpose: it stays until it is answered or declined, and Escape does neither.
export function IncomingCallDialog({ open, calls }: IncomingCallDialogProps) {
  const peerName = useCallStore((state) => state.peerName) ?? ''
  const media = useCallStore((state) => state.media)
  const video = media === 'Video'

  return (
    <Dialog open={open} onClose={() => undefined} title="Incoming call" dismissible={false}>
      <div className="flex flex-col items-center gap-4 px-5 py-8 text-center">
        <Avatar name={peerName} size="lg" />
        <div>
          <p className="text-lg font-semibold text-slate-900">{peerName}</p>
          <p className="text-sm text-slate-500" aria-live="polite">
            Incoming {video ? 'video' : 'audio'} call
          </p>
        </div>

        <div className="flex w-full gap-3">
          <Button variant="danger" fullWidth onClick={() => calls.end()}>
            <PhoneOff className="h-5 w-5" aria-hidden="true" />
            Decline
          </Button>
          <Button variant="success" fullWidth onClick={() => void calls.accept()}>
            {video ? <Video className="h-5 w-5" aria-hidden="true" /> : <Phone className="h-5 w-5" aria-hidden="true" />}
            Accept
          </Button>
        </div>

        {video && (
          <Button variant="ghost" size="sm" onClick={() => void calls.accept(false)}>
            Answer without video
          </Button>
        )}
      </div>
    </Dialog>
  )
}
