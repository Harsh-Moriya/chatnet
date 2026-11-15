import type { CallEndReason, CallMedia } from '../chat/types'

// What the hub sends and takes for a call. These mirror the JSON (see docs/features/calls.md); the controller is the only code that
// talks to the hub through them.

export type SignalType = 'Offer' | 'Answer' | 'Candidate'

// An entry of the list RTCPeerConnection takes as `iceServers`. A STUN entry has no credentials.
export interface IceServer {
  urls: string[]
  username?: string
  credential?: string
}

export interface CallStarted {
  callId: string
  rejoinToken: string
  ringsUntil: string
  iceServers: IceServer[]
}

export interface CallAccepted {
  rejoinToken: string
  media: CallMedia
  callerId: string
  iceServers: IceServer[]
}

export interface CallRejoined {
  state: 'Ringing' | 'Active' | 'Ending'
  media: CallMedia
  iceServers: IceServer[]
}

export interface IncomingCall {
  callId: string
  conversationId: string
  callerId: string
  callerDisplayName: string
  media: CallMedia
  ringsUntil: string
}

export interface CallEnded {
  callId: string
  reason: CallEndReason
  endedAt: string
}

// The methods of a call on the hub. Each refuses with a HubException whose message holds one of the codes in callErrors.ts.
export interface CallsApi {
  startCall: (conversationId: string, media: CallMedia) => Promise<CallStarted>
  acceptCall: (callId: string) => Promise<CallAccepted>
  endCall: (callId: string, failed: boolean) => Promise<void>
  // True when the other side got it; false while it is away or the call is not active.
  sendSignal: (callId: string, type: SignalType, payload: string) => Promise<boolean>
  rejoinCall: (callId: string, rejoinToken: string) => Promise<CallRejoined>
}

// What the hub tells the browser about calls. Registered with the other hub handlers in chatConnection.ts.
export interface CallEvents {
  onIncomingCall: (call: IncomingCall) => void
  onCallAnswered: (callId: string) => void
  onCallSignal: (callId: string, type: SignalType, payload: string) => void
  onCallEnded: (ended: CallEnded) => void
  // The hub connection came back after a drop: a side of a call that was running takes it back (RejoinCall).
  onReconnected: () => void
}
