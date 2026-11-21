import { create } from 'zustand'
import type { CallEndReason, CallMedia } from '../chat/types'

// Where a call stands in this browser. A pure state machine: it holds no timers and makes no requests, and the controller drives it.
//
//   idle -> starting -> outgoing -> connecting -> active -> ended -> idle     (the caller)
//   idle -> incoming -> connecting -> active -> ended -> idle                 (the callee)
//
// `starting` is the caller checking keys and opening the microphone before anything rings; `ended` stays long enough to say how it ended.
export type CallPhase = 'idle' | 'starting' | 'outgoing' | 'incoming' | 'connecting' | 'active' | 'ended'

export interface CallPeople {
  conversationId: string
  peerId: string
  peerName: string
  media: CallMedia
}

interface CallState extends Partial<CallPeople> {
  phase: CallPhase
  callId: string | null
  // True when this browser made the call.
  outgoing: boolean
  // When it stops ringing, an ISO time. The server ends the ring; this is kept for a screen that shows a countdown, which none does yet.
  ringsUntil: string | null
  // The browser's clock when it was answered, for the time shown.
  answeredAt: number | null
  // Whether this side sends video: a video call answered as audio only does not.
  sendingVideo: boolean
  muted: boolean
  cameraOff: boolean
  // The media dropped in a call that had connected, and is being brought back.
  reconnecting: boolean
  endedReason: CallEndReason | null
  // What the person must be told (a refusal, a failure, how it ended), or null.
  notice: string | null
  localStream: MediaStream | null
  remoteStream: MediaStream | null
  // The call is shown as a bar over the chat instead of the full screen. A choice for this call only: the next one starts full screen.
  minimised: boolean

  begin: (people: CallPeople) => void
  ring: (call: CallPeople & { callId: string; ringsUntil: string }) => void
  calling: (callId: string, ringsUntil: string) => void
  answered: (now: number, sendingVideo?: boolean) => void
  connected: () => void
  finish: (reason: CallEndReason | null, notice: string | null) => void
  clear: () => void
  setMuted: (muted: boolean) => void
  setCameraOff: (off: boolean) => void
  setReconnecting: (reconnecting: boolean) => void
  setStreams: (streams: { local?: MediaStream | null; remote?: MediaStream | null }) => void
  setNotice: (notice: string | null) => void
  setMinimised: (minimised: boolean) => void
}

const IDLE = {
  phase: 'idle' as CallPhase,
  callId: null,
  outgoing: false,
  ringsUntil: null,
  answeredAt: null,
  sendingVideo: false,
  muted: false,
  cameraOff: false,
  reconnecting: false,
  endedReason: null,
  notice: null,
  localStream: null,
  remoteStream: null,
  minimised: false,
  conversationId: undefined,
  peerId: undefined,
  peerName: undefined,
  media: undefined,
}

const HOLDS_A_CALL: ReadonlySet<CallPhase> = new Set(['starting', 'outgoing', 'connecting', 'active'])

export const useCallStore = create<CallState>()((set) => ({
  ...IDLE,

  // The caller starts: a person and a kind of call, not yet a call (the server has not made one).
  begin: (people) =>
    set((state) => (state.phase !== 'idle' ? state : { ...IDLE, ...people, phase: 'starting', outgoing: true, sendingVideo: people.media === 'Video' })),

  ring: (call) =>
    set((state) =>
      state.phase !== 'idle' ? state : { ...IDLE, ...call, phase: 'incoming', outgoing: false, sendingVideo: call.media === 'Video' },
    ),

  calling: (callId, ringsUntil) => set((state) => (state.phase !== 'starting' ? state : { phase: 'outgoing', callId, ringsUntil })),

  // The call was answered, by the callee's browser for the caller, and by this one for the callee: the media is being set up. For a callee
  // that answers a video call as audio only, `sendingVideo` is false.
  answered: (now, sendingVideo) =>
    set((state) =>
      state.phase !== 'outgoing' && state.phase !== 'incoming'
        ? state
        : { phase: 'connecting', answeredAt: now, ringsUntil: null, sendingVideo: sendingVideo ?? state.sendingVideo },
    ),

  connected: () => set((state) => (state.phase !== 'connecting' ? state : { phase: 'active' })),

  finish: (reason, notice) =>
    set((state) =>
      state.phase === 'idle' || state.phase === 'ended' ? state : { phase: 'ended', endedReason: reason, notice, ringsUntil: null, reconnecting: false },
    ),

  clear: () => set((state) => (state.phase === 'idle' ? state : { ...IDLE })),

  setMuted: (muted) => set((state) => (state.muted === muted ? state : { muted })),
  setCameraOff: (cameraOff) => set((state) => (state.cameraOff === cameraOff ? state : { cameraOff })),
  setReconnecting: (reconnecting) => set((state) => (state.reconnecting === reconnecting ? state : { reconnecting })),
  setStreams: ({ local, remote }) =>
    set((state) => ({
      localStream: local === undefined ? state.localStream : local,
      remoteStream: remote === undefined ? state.remoteStream : remote,
    })),
  setNotice: (notice) => set((state) => (state.notice === notice ? state : { notice })),

  // Only a call this person is part of can be put away: a ringing call must be answered where it rings, and one that has ended has nothing
  // left to hold. An ended call keeps the flag, so a call that ends while minimised says so in the bar.
  setMinimised: (minimised) =>
    set((state) => (state.minimised === minimised || !HOLDS_A_CALL.has(state.phase) ? state : { minimised })),
}))

// Whether a call is being made, rung, or running: what decides whether another can be started or rung.
export const inCall = (phase: CallPhase): boolean => phase !== 'idle' && phase !== 'ended'
