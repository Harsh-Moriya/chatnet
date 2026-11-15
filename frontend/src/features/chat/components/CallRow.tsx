import { Phone, PhoneMissed, Video } from 'lucide-react'
import { cn } from '../../../lib/cn'
import { formatClock } from '../../../lib/format'
import { callDuration, callLabel } from '../calls'
import type { CallInfo } from '../types'

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
        {onCallBack !== undefined && (
          <button
            type="button"
            onClick={onCallBack}
            className="rounded-full px-2 py-0.5 font-semibold text-blue-600 hover:bg-blue-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500"
          >
            Call back
          </button>
        )}
      </span>
    </div>
  )
}
