import { formatTime } from './viewer/videoView'
import type { CallInfo, Message } from './types'

// How a finished call is told in the conversation, as plain functions so the words can be tested without a browser. The server decides what
// a missed call is (CallInfo.isMissed); these only decide what to say about the call.

// Whether a message counts toward the unread messages of the person who did not send it: any text, and a call only when it was missed. The
// server counts the same way when it sends the conversation list.
export function countsAsUnread(message: Pick<Message, 'kind' | 'call'>): boolean {
  return message.kind !== 'Call' || message.call?.isMissed === true
}

// How long the call ran, in whole seconds, or null for a call nobody answered.
export function callSeconds(call: CallInfo): number | null {
  if (call.answeredAt === null) return null

  const milliseconds = new Date(call.endedAt).getTime() - new Date(call.answeredAt).getTime()
  return Math.max(0, Math.round(milliseconds / 1000))
}

// "4:12" for a call that was answered; null otherwise.
export function callDuration(call: CallInfo): string | null {
  const seconds = callSeconds(call)
  return seconds === null ? null : formatTime(seconds)
}

// What the call was, from the point of view of the reader: `own` is true when they were the caller. A call that was answered is just that
// kind of call (and "Dropped" when the connection broke); one nobody answered says how it ended, which differs for the person who called
// and the person who was called.
export function callLabel(call: CallInfo, own: boolean): string {
  const kind = call.media === 'Video' ? 'video call' : 'audio call'

  if (call.answeredAt !== null) {
    return call.endReason === 'Failed' ? `Dropped ${kind}` : `${kind[0].toUpperCase()}${kind.slice(1)}`
  }

  if (own) {
    switch (call.endReason) {
      case 'Declined':
        return 'Call declined'
      case 'Busy':
        return 'Line busy'
      case 'Missed':
        return 'No answer'
      case 'Cancelled':
        return `Cancelled ${kind}`
      default:
        return 'Call failed'
    }
  }

  return call.endReason === 'Declined' ? `Declined ${kind}` : `Missed ${kind}`
}

// The line of the sidebar for a conversation whose last message is a call.
export function callPreview(call: CallInfo, own: boolean): string {
  const duration = callDuration(call)
  return duration === null ? callLabel(call, own) : `${callLabel(call, own)}, ${duration}`
}
