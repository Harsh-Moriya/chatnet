import { signCall, type CallRole } from '../../lib/crypto/call'
import { newEd25519, type KeyPair } from '../../lib/crypto/primitives'
import { createCallController, type CallController } from './callController'
import { useCallStore } from './callStore'
import { MediaUnavailable, type MediaSource } from './media'
import { PeerIdentityChanged, PeerKeysUnavailable, type CallIdentity } from './peerIdentity'
import type { PeerFactory, PeerHandlers, PeerSession, PeerState } from './peerSession'
import type { Ringtone, RingKind } from './ringtone'
import { encodeSdpSignal, type CandidateInit } from './signals'
import type { CallAccepted, CallRejoined, CallStarted, CallsApi, IceServer, IncomingCall, SignalType } from './types'

// The fakes a test of the call controller stands on: the hub, the WebRTC connection, the microphone, the ring, the clock and the keys. Real
// cryptography is used for the signatures, so what the controller signs and checks is really signed and checked.

export const SELF = '3f2a9c1e-7b4d-4e8a-9c55-0a1b2c3d4e5f'
export const OTHER = '9d8c7b6a-5f4e-4d3c-8b2a-1f0e9d8c7b6a'
export const CONVERSATION = 'c0ffee00-1234-4abc-8def-0123456789ab'
export const CALL = 'ca11ca11-0000-4000-8000-000000000001'

export const OFFER_SDP = 'v=0\r\na=fingerprint:sha-256 11:11:11:11\r\na=setup:actpass\r\n'
export const ANSWER_SDP = 'v=0\r\na=fingerprint:sha-256 22:22:22:22\r\na=setup:active\r\n'
// What a restart offer looks like: the same certificate, new ICE credentials.
export const RESTART_SDP = 'v=0\r\na=fingerprint:sha-256 11:11:11:11\r\na=ice-ufrag:again\r\na=setup:actpass\r\n'

// ##### Media #####

export class FakeTrack {
  enabled = true
  stopped = false

  readonly kind: 'audio' | 'video'

  constructor(kind: 'audio' | 'video') {
    this.kind = kind
  }

  private readonly listeners: (() => void)[] = []

  stop() {
    this.stopped = true
  }

  addEventListener(type: string, listener: () => void) {
    if (type === 'ended') this.listeners.push(listener)
  }

  // The device behind the track went away: the browser ends the track by itself (stop() does not do this).
  end() {
    this.listeners.forEach((listener) => listener())
  }
}

export class FakeStream {
  readonly tracks: FakeTrack[]

  constructor(kinds: ('audio' | 'video')[]) {
    this.tracks = kinds.map((kind) => new FakeTrack(kind))
  }

  getTracks = () => [...this.tracks]
  getAudioTracks = () => this.tracks.filter((track) => track.kind === 'audio')
  getVideoTracks = () => this.tracks.filter((track) => track.kind === 'video')
  addTrack = (track: FakeTrack) => void this.tracks.push(track)
  removeTrack = (track: FakeTrack) => void this.tracks.splice(this.tracks.indexOf(track), 1)
}

export class FakeMedia implements MediaSource {
  captures: boolean[] = []
  streams: FakeStream[] = []
  failWith: MediaUnavailable | null = null
  devices: { kind: 'audio' | 'video'; deviceId: string; track: FakeTrack }[] = []
  log: string[] = []

  async capture(video: boolean) {
    this.log.push('capture')
    this.captures.push(video)
    if (this.failWith) throw this.failWith

    const stream = new FakeStream(video ? ['audio', 'video'] : ['audio'])
    this.streams.push(stream)
    return stream as unknown as MediaStream
  }

  // What the default device gave, one track per call; `noDefault` makes the browser say there is none.
  defaults: FakeTrack[] = []
  noDefault = false

  async captureDefault(kind: 'audio' | 'video') {
    if (this.noDefault) throw new MediaUnavailable('missing')

    const track = new FakeTrack(kind)
    this.defaults.push(track)
    return track as unknown as MediaStreamTrack
  }

  async captureDevice(kind: 'audio' | 'video', deviceId: string) {
    if (this.failWith) throw this.failWith

    const track = new FakeTrack(kind)
    this.devices.push({ kind, deviceId, track })
    return track as unknown as MediaStreamTrack
  }
}

// ##### The hub #####

export class FakeApi implements CallsApi {
  calls: string[] = []
  signals: { callId: string; type: SignalType; payload: string }[] = []
  ended: { callId: string; failed: boolean }[] = []
  rejoined: { callId: string; token: string }[] = []
  started: { conversationId: string; media: string }[] = []

  // What each method answers; a test replaces one to refuse or to hold the answer back.
  startResult: () => Promise<CallStarted> = async () => ({ callId: CALL, rejoinToken: 'caller-token', ringsUntil: '2030-01-01T00:00:30Z', iceServers: [] as IceServer[] })
  acceptResult: () => Promise<CallAccepted> = async () => ({ rejoinToken: 'callee-token', media: 'Audio', callerId: OTHER, iceServers: [] })
  rejoinResult: () => Promise<CallRejoined> = async () => ({ state: 'Active', media: 'Audio', iceServers: [] })
  // False while the other side is away: the signal is not handed on.
  delivers = true

  async startCall(conversationId: string, media: 'Audio' | 'Video') {
    this.calls.push('startCall')
    this.started.push({ conversationId, media })
    return this.startResult()
  }

  async acceptCall(callId: string) {
    this.calls.push(`acceptCall:${callId}`)
    return this.acceptResult()
  }

  async endCall(callId: string, failed: boolean) {
    this.calls.push('endCall')
    this.ended.push({ callId, failed })
  }

  async sendSignal(callId: string, type: SignalType, payload: string) {
    this.calls.push(`signal:${type}`)
    this.signals.push({ callId, type, payload })
    return this.delivers
  }

  async rejoinCall(callId: string, token: string) {
    this.calls.push('rejoinCall')
    this.rejoined.push({ callId, token })
    return this.rejoinResult()
  }
}

export const refusal = (code: string) => new Error(`An unexpected error occurred invoking 'X' on the server. HubException: ${code}`)

// ##### The connection #####

export class FakePeer implements PeerSession {
  tracks: { kind: string; stream: unknown }[] = []
  offersMade = 0
  acceptedOffers: string[] = []
  acceptedAnswers: string[] = []
  restartedWith: IceServer[][] = []
  // Found by the browser as it makes a description, which is when it starts gathering: they are reported before the description is returned.
  foundWhileDescribing: CandidateInit[] = []
  candidates: CandidateInit[] = []
  replaced: { kind: string; track: unknown }[] = []
  closed = false
  log: string[] = []

  readonly iceServers: IceServer[]
  readonly handlers: PeerHandlers

  constructor(iceServers: IceServer[], handlers: PeerHandlers) {
    this.iceServers = iceServers
    this.handlers = handlers
  }

  addTrack(track: MediaStreamTrack, stream: MediaStream) {
    this.tracks.push({ kind: track.kind, stream })
  }

  private gather() {
    for (const candidate of this.foundWhileDescribing) this.handlers.onCandidate(candidate)
  }

  async createOffer() {
    this.gather()
    this.offersMade++
    this.log.push('createOffer')
    return OFFER_SDP
  }

  async acceptOffer(sdp: string) {
    this.gather()
    this.acceptedOffers.push(sdp)
    this.log.push('acceptOffer')
    return ANSWER_SDP
  }

  async acceptAnswer(sdp: string) {
    this.acceptedAnswers.push(sdp)
    this.log.push('acceptAnswer')
  }

  async restartOffer(iceServers: IceServer[]) {
    this.gather()
    this.restartedWith.push(iceServers)
    this.log.push('restartOffer')
    return RESTART_SDP
  }

  // The connection says how it is: what the browser does as the network comes and goes.
  state(state: PeerState) {
    this.handlers.onState(state)
  }

  async addCandidate(candidate: CandidateInit) {
    this.candidates.push(candidate)
    this.log.push(`candidate ${candidate.candidate}`)
  }

  async replaceTrack(kind: 'audio' | 'video', track: MediaStreamTrack) {
    this.replaced.push({ kind, track })
  }

  close() {
    this.closed = true
  }
}

export class FakePeers {
  peers: FakePeer[] = []

  factory: PeerFactory = (iceServers, handlers) => {
    const peer = new FakePeer(iceServers, handlers)
    this.peers.push(peer)
    return peer
  }

  get current(): FakePeer {
    return this.peers[this.peers.length - 1]
  }
}

// ##### The ring and the clock #####

export class FakeRingtone implements Ringtone {
  events: string[] = []
  start = (kind: RingKind) => void this.events.push(`start:${kind}`)
  stop = () => void this.events.push('stop')

  // What is ringing now: the last thing the ring was told, if it was told to start.
  get ringing(): RingKind | null {
    const last = this.events.at(-1)
    return last === 'start:incoming' ? 'incoming' : last === 'start:outgoing' ? 'outgoing' : null
  }
}

export class ManualTimers {
  pending: { run: () => void; ms: number; cancelled: boolean; done: boolean }[] = []

  after = (run: () => void, ms: number) => {
    const timer = { run, ms, cancelled: false, done: false }
    this.pending.push(timer)
    return () => {
      timer.cancelled = true
    }
  }

  // Runs the timers that are waiting (and not cancelled) whose delay is at least / at most the given.
  fire(filter: (ms: number) => boolean = () => true) {
    for (const timer of this.pending.filter((t) => !t.cancelled && !t.done && filter(t.ms))) {
      timer.done = true
      timer.run()
    }
  }

  waiting(filter: (ms: number) => boolean = () => true) {
    return this.pending.filter((t) => !t.cancelled && !t.done && filter(t.ms)).length
  }
}

// ##### Keys #####

export class FakeIdentity implements CallIdentity {
  failWith: Error | null = null

  readonly self: string
  readonly keys: KeyPair
  peerKey: Uint8Array

  constructor(self: string, keys: KeyPair, peerKey: Uint8Array) {
    this.self = self
    this.keys = keys
    this.peerKey = peerKey
  }

  selfId = () => this.self
  signer = () => this.keys

  async peerIdentity() {
    if (this.failWith) throw this.failWith
    return this.peerKey
  }
}

export const keysUnavailable = () => new PeerKeysUnavailable()
export const identityChanged = () => new PeerIdentityChanged()

// ##### The rig #####

export interface Rig {
  controller: CallController
  api: FakeApi
  peers: FakePeers
  media: FakeMedia
  ringtone: FakeRingtone
  timers: ManualTimers
  identity: FakeIdentity
  // The other person's keys, to sign what they send.
  other: KeyPair
  incoming: IncomingCall[]
  // The calls whose alert was taken down.
  alertsClosed: string[]
  clock: { now: number }
}

export async function createRig(): Promise<Rig> {
  useCallStore.getState().clear()

  const [self, other] = await Promise.all([newEd25519(), newEd25519()])
  const api = new FakeApi()
  const peers = new FakePeers()
  const media = new FakeMedia()
  const ringtone = new FakeRingtone()
  const timers = new ManualTimers()
  const identity = new FakeIdentity(SELF, self, other.publicKey)
  const incoming: IncomingCall[] = []
  const alertsClosed: string[] = []
  const clock = { now: Date.parse('2030-01-01T00:00:00Z') }

  const controller = createCallController({
    api: () => api,
    peers: peers.factory,
    media,
    ringtone,
    identity,
    now: () => clock.now,
    after: timers.after,
    onIncoming: (call) => {
      incoming.push(call)
      return () => void alertsClosed.push(call.callId)
    },
  })

  return { controller, api, peers, media, ringtone, timers, identity, other, incoming, alertsClosed, clock }
}

// What the other person's browser would send: a description signed by their key for this call, in the direction given.
export async function signedByOther(rig: Rig, role: CallRole, sdp: string, overrides: { callId?: string; key?: KeyPair } = {}): Promise<string> {
  const context = { callId: overrides.callId ?? CALL, conversationId: CONVERSATION, signerId: OTHER, peerId: SELF }
  return encodeSdpSignal(sdp, await signCall(overrides.key ?? rig.other, context, role, sdp))
}

export const incomingCall = (overrides: Partial<IncomingCall> = {}): IncomingCall => ({
  callId: CALL,
  conversationId: CONVERSATION,
  callerId: OTHER,
  callerDisplayName: 'Ben Caller',
  media: 'Audio',
  ringsUntil: '2030-01-01T00:00:30Z',
  ...overrides,
})

// Lets the promises the controller is waiting on run, including the signatures, which the browser's crypto makes off the main thread.
export const settle = async () => {
  for (let i = 0; i < 20; i++) await Promise.resolve()
  await new Promise((resolve) => setTimeout(resolve, 25))
}
