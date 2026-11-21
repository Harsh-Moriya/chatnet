import { Phone, PhoneMissed, Video } from 'lucide-react'
import { Button } from '../../../components/ui/Button'
import { cn } from '../../../lib/cn'
import { formatClock } from '../../../lib/format'
import { callDuration, callLabel } from '../calls'
import type { CallInfo } from '../types'
import { Pill } from '../../../components/ui/Pill'

interface CallRowProps {
  call: CallInfo
  // True when the signed-in user made the call.
  own: boolean
  // Calls the person back with the same kind of call; absent where a call cannot be made (a conversation that was left, a dropped connection).
  onCallBack?: () => void
}

// A finished call as a line of its own in the conversation: what it was, when it ended, and how long it ran. A call that was missed
// stands out for the person who was called.
export function CallRow({ call, own, onCallBack }: CallRowProps) {
  const missedByMe = call.isMissed && !own
  const duration = callDuration(call)
  const Icon = missedByMe ? PhoneMissed : call.media === 'Video' ? Video : Phone

  return (
    <div className="my-2 flex justify-center">
      <Pill tone="chip" size="md" className={cn(missedByMe && 'text-danger-fg')}>
        <Icon className="h-3.5 w-3.5 shrink-0" aria-hidden="true" />
        <span>{callLabel(call, own)}</span>
        {duration !== null && <span>{duration}</span>}
        <time dateTime={call.endedAt} className="font-normal">
          {formatClock(call.endedAt)}
        </time>
        {onCallBack !== undefined && (
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={onCallBack}
            className="h-auto! rounded-full! px-2! py-0.5! text-xs font-semibold text-accent-fg! hover:bg-accent-soft!"
          >
            Call back
          </Button>
        )}
      </Pill>
    </div>
  )
}
