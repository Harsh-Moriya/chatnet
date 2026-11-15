import type { CallEndReason } from '../chat/types'

// The words the person is told about a call, as plain functions so they can be tested.

export const NOT_VERIFIED = 'This call could not be verified.'

export const MEDIA_NOTICES = {
  denied: 'Allow the microphone (and the camera for a video call) in your browser to make or answer calls.',
  missing: 'No microphone was found. Connect one and try again.',
  busy: 'Your microphone or camera is in use by something else.',
  unsupported: 'This browser cannot make calls.',
  other: 'The microphone or camera could not be opened.',
} as const

// What the person sees when a call they were in has ended. Null when there is nothing to say beyond "Call ended": they hung up, or the
// other person did after a call that went well.
export function endedNotice(input: { reason: CallEndReason | null; outgoing: boolean; answered: boolean; peerName: string }): string | null {
  switch (input.reason) {
    case 'Declined':
      return `${input.peerName} declined the call.`
    case 'Missed':
      return `${input.peerName} did not answer.`
    case 'Busy':
      return `${input.peerName} is in another call.`
    case 'Failed':
      return input.answered ? 'The call was dropped.' : 'The call could not be connected.'
    default:
      return null
  }
}

export function startRefusal(code: string | null, peerName: string): string {
  switch (code) {
    case 'already-in-a-call':
      return 'You are already in a call.'
    case 'callee-busy':
      return `${peerName} is in another call.`
    case 'peer-unavailable':
      return `${peerName} cannot be called.`
    case 'rate-limited':
      return 'You are calling too often. Try again in a minute.'
    case 'calls-unavailable':
      return 'Calls are not available right now. Try again in a moment.'
    default:
      return 'The call could not be started.'
  }
}

// A microphone or camera that was being sent stopped by itself (unplugged, or taken by something else): what the person is told, whether the
// default one was put in its place or there was none.
export function deviceLostNotice(kind: 'audio' | 'video', replaced: boolean): string {
  const device = kind === 'audio' ? 'microphone' : 'camera'
  return replaced ? `Your ${device} was disconnected, so the default one is used.` : `Your ${device} was disconnected.`
}
