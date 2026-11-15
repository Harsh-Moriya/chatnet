import { Phone, PhoneMissed, Video } from 'lucide-react'
import { cn } from '../../../lib/cn'
import { formatClock } from '../../../lib/format'
import { callDuration, callLabel } from '../calls'
import type { CallInfo } from '../types'

interface CallRowProps {
  call: CallInfo
  // True when the signed-in user made the call.
  own: boolean
}

// A finished call as a line of its own in the conversation: what it was, when it ended, and how long it ran. A call that was missed
// stands out for the person who was called.
export function CallRow({ call, own }: CallRowProps) {
  const missedByMe = call.isMissed && !own
  const duration = callDuration(call)
  const Icon = missedByMe ? PhoneMissed : call.media === 'Video' ? Video : Phone

  return (
    <div className="my-2 flex justify-center">
      <span
        className={cn(
          'inline-flex items-center gap-1.5 rounded-full bg-slate-200/70 px-3 py-1 text-xs font-medium',
          missedByMe ? 'text-red-500' : 'text-slate-500',
        )}
      >
        <Icon className="h-3.5 w-3.5 shrink-0" aria-hidden="true" />
        <span>{callLabel(call, own)}</span>
        {duration !== null && <span>{duration}</span>}
        <time dateTime={call.endedAt} className="font-normal">
          {formatClock(call.endedAt)}
        </time>
      </span>
    </div>
  )
}
