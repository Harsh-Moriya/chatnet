import { signCall, verifyCall, type CallContext, type CallRole } from '../../lib/crypto/call'
import type { CallMedia } from '../chat/types'
import { callErrorCode } from './callErrors'
import { useCallStore } from './callStore'
import { NOT_VERIFIED, deviceLostNotice, endedNotice, startRefusal } from './callTexts'
import { MediaUnavailable, stopStream, type MediaSource } from './media'
import { PeerIdentityChanged, PeerKeysUnavailable, type CallIdentity } from './peerIdentity'
import type { PeerFactory, PeerSession, PeerState } from './peerSession'
import type { Ringtone } from './ringtone'
import { decodeCandidate, decodeSdpSignal, encodeCandidate, encodeSdpSignal, type CandidateInit } from './signals'
import type { CallEnded, CallEvents, CallsApi, IceServer, IncomingCall, SignalType } from './types'

// The call logic of the browser, with every dependency passed in so a test drives it with fakes: the hub (CallsApi), the WebRTC connection,
// the microphone and camera, the ring, the keys and the clock. It owns the streams, the connection and the timers of the one call this
// browser is in, and writes where it stands to the call store for the screens.
//
// The rules it keeps:
//  - Nothing rings anyone until the microphone is open and the other person's keys are known: a refused permission, or a person who has not
//    set up encryption, starts no call.
//  - The caller makes its offer only after the call was answered (CallAnswered), so nothing is captured or sent to someone who never answers.
//  - Every session description is signed over its fingerprints and checked against the other person's identity key before it is used; a
//    description that is not accepted ends the call as failed.
//  - Signals are handled one after another, in the order they arrive; an offer that arrives before the answer's connection exists is held
//    until it does. A side sends its candidates only after the description they belong to, so the other side never has a candidate for a
//    description it has not got.
//  - When the media drops, the caller (only the caller, so the two never offer at once) restarts ICE with a new signed offer, and the call is
//    ended as failed only if that does not bring the media back in time. The caller also offers again while it has no answer: a signal handed
//    to a connection that was just closing is counted as delivered and never arrives.
//  - A call that is not connected within MEDIA_DOWN_MS of being answered is ended as failed, whatever state its connection says it is in.

// How long the media may be down (disconnected or failed) before the call is ended as failed. Long enough for the hub connection to come
// back (it retries at 0, 2 and 7 seconds) and for a restart to follow.
export const MEDIA_DOWN_MS = 15000

// How long the caller waits after the media went `disconnected` before restarting: a short drop often heals by itself. `failed` restarts at once.
export const RESTART_AFTER_MS = 2000

// How often the caller tries again while the media is still down (a restart that was not handed on, or did not work).
export const RESTART_RETRY_MS = 4000

// The most restarts of one call, so a connection that cannot be repaired does not fill the server's limit of signals for the call.
export const MAX_RESTARTS = 20

// How long a finished call stays on the screen, to say how it ended.
export const ENDED_SHOWN_MS = 4000

// How long after a call should have stopped ringing a screen that nobody told is taken down.
const RING_GRACE_MS = 3000

export interface CallDeps {
  api: () => CallsApi | null
  peers: PeerFactory
  media: MediaSource
  ringtone: Ringtone
  identity: CallIdentity
  now: () => number
  // Runs `run` after `ms`; returns what cancels it.
  after: (run: () => void, ms: number) => () => void
  // A call is ringing here: the place to alert the person if the page is not in front.
  onIncoming?: (call: IncomingCall) => void
}

export interface StartCallInput {
  conversationId: string
  peerId: string
  peerName: string
  media: CallMedia
}

export interface CallController extends CallEvents {
  start: (input: StartCallInput) => Promise<void>
  // Answers the call that is ringing. `withVideo` false answers a video call as audio only.
  accept: (withVideo?: boolean) => Promise<void>
  // Hangs up, cancels, or declines, whichever the state of the call makes it.
  end: () => void
  setMuted: (muted: boolean) => void
  setCameraOff: (off: boolean) => void
  switchDevice: (kind: 'audio' | 'video', deviceId: string) => Promise<void>
  // The page is closing or reloading: a call this side made or answered is ended at once, so the other person is not left waiting out the
  // server's grace. A call that is only ringing here is left alone: nothing was answered, and the page that opens next is told about it.
  leave: () => void
  // The call screen is dismissed, or the page is going away: forget everything without telling anyone.
  dispose: () => void
}

// The other side's description did not carry a signature that checks out.
class NotVerified extends Error {
  constructor() {
    super(NOT_VERIFIED)
    this.name = 'NotVerified'
  }
}

interface Session {
  // Null until the server has made the call (the caller is still starting).
  callId: string | null
  conversationId: string
  peerId: string
  peerName: string
  media: CallMedia
  outgoing: boolean
  token: string | null
  iceServers: IceServer[]
  peer: PeerSession | null
  local: MediaStream | null
  answered: boolean
  // This tab pressed Answer and is waiting for the server: the CallAnswered it will get is its own, not another tab's.
  accepting: boolean
  // The local candidates found while a description is being made and signed: they go out after it, never before.
  queuedCandidates: CandidateInit[] | null
  // What the connection last said about itself.
  peerState: PeerState
  // How many times the caller has restarted ICE.
  restarts: number
  // The caller has sent an offer and the answer has not been applied yet.
  awaitingAnswer: boolean
  cancelRestart: (() => void) | null
  heldSignals: { type: SignalType; payload: string }[]
  chain: Promise<void>
  cancelMediaDown: (() => void) | null
  cancelRingEnd: (() => void) | null
}

export function createCallController(deps: CallDeps): CallController {
  const store = () => useCallStore.getState()
  let session: Session | null = null
  let cancelClear: (() => void) | null = null

  // ##### Ending, on this side #####

  function teardown(s: Session) {
    s.cancelMediaDown?.()
    s.cancelMediaDown = null
    s.cancelRestart?.()
    s.cancelRestart = null
    s.cancelRingEnd?.()
    s.cancelRingEnd = null
    s.peer?.close()
    s.peer = null
    stopStream(s.local)
    s.local = null
    deps.ringtone.stop()
    store().setStreams({ local: null, remote: null })
  }

  // The call is over for this browser, whatever the server says next (it is told, and its CallEnded finds nothing to do).
  function finish(s: Session, reason: 'Failed' | null, notice: string | null) {
    if (session !== s) return

    const phase = store().phase
    teardown(s)
    session = null

    // A call that was only ringing here goes without a word: nothing was in progress.
    if (phase === 'incoming') {
      store().clear()
      return
    }

    store().finish(reason, notice)
    scheduleClear()
  }

  function scheduleClear() {
    cancelClear?.()
    cancelClear = deps.after(() => {
      cancelClear = null
      if (session === null) store().clear()
    }, ENDED_SHOWN_MS)
  }

  function tellServerEnd(s: Session, failed: boolean) {
    if (s.callId !== null) deps.api()?.endCall(s.callId, failed).catch(() => {})
  }

  // Why a call that was in progress cannot go on: said to the person, and to the server as a failure.
  function fail(s: Session, error: unknown) {
    if (session !== s) return

    const notice =
      error instanceof PeerIdentityChanged
        ? `The security keys of ${s.peerName} changed. Compare the safety number, then call again.`
        : error instanceof NotVerified || error instanceof PeerKeysUnavailable
          ? NOT_VERIFIED
          : 'The call could not be connected.'

    tellServerEnd(s, true)
    finish(s, 'Failed', notice)
  }

  // ##### The connection #####

  function context(s: Session, signer: 'self' | 'peer'): CallContext {
    const self = deps.identity.selfId()
    return {
      callId: s.callId as string,
      conversationId: s.conversationId,
      signerId: signer === 'self' ? self : s.peerId,
      peerId: signer === 'self' ? s.peerId : self,
    }
  }

  // Whether the signal was handed to the other side: false when it is away, or when it could not be sent at all.
  function send(s: Session, type: SignalType, payload: string): Promise<boolean> {
    const sent = s.callId === null ? undefined : deps.api()?.sendSignal(s.callId, type, payload)
    return sent === undefined ? Promise.resolve(false) : sent.catch(() => false)
  }

  // Makes a description, signs it and sends it, and only then the candidates that were found meanwhile: the other side would otherwise get
  // candidates (with new credentials, after a restart) for a description it does not have yet.
  async function sendDescription(s: Session, type: 'Offer' | 'Answer', make: () => Promise<string>) {
    s.queuedCandidates = []

    try {
      const sdp = await make()
      const signature = await signCall(deps.identity.signer(), context(s, 'self'), type === 'Offer' ? 'offer' : 'answer', sdp)
      await send(s, type, encodeSdpSignal(sdp, signature))

      // Whether it was handed on is not proof that it arrived (the other side's connection may have been closing), so an offer that is not
      // answered in time is made again.
      if (type === 'Offer') {
        s.awaitingAnswer = true
        armRestart(s, RESTART_RETRY_MS)
      }
    } finally {
      const queued = s.queuedCandidates
      s.queuedCandidates = null
      if (session === s) for (const candidate of queued) void send(s, 'Candidate', encodeCandidate(candidate))
    }
  }

  function connect(s: Session) {
    const local = s.local as MediaStream
    const peer = deps.peers(s.iceServers, {
      onCandidate: (candidate) => {
        if (session !== s) return

        if (s.queuedCandidates !== null) s.queuedCandidates.push(candidate)
        else void send(s, 'Candidate', encodeCandidate(candidate))
      },
      onTrack: (stream) => {
        if (session === s) store().setStreams({ remote: stream })
      },
      onState: (state) => onPeerState(s, state),
    })

    local.getTracks().forEach((track) => peer.addTrack(track, local))
    s.peer = peer
    armMediaDown(s)
  }

  function onPeerState(s: Session, state: PeerState) {
    if (session !== s) return
    s.peerState = state

    if (state === 'connected') {
      s.cancelMediaDown?.()
      s.cancelMediaDown = null
      s.cancelRestart?.()
      s.cancelRestart = null
      store().setReconnecting(false)
      store().connected()
      return
    }

    if (state !== 'disconnected' && state !== 'failed') return

    if (store().phase === 'active') store().setReconnecting(true)

    armMediaDown(s)

    // Only the caller offers, so the two sides never offer at once. A drop usually heals itself within a moment; a failure does not.
    if (s.outgoing && (s.cancelRestart === null || state === 'failed')) armRestart(s, state === 'failed' ? 0 : RESTART_AFTER_MS)
  }

  // The call is ended as failed if the media is not up within MEDIA_DOWN_MS: of being answered, or of a drop.
  function armMediaDown(s: Session) {
    if (s.cancelMediaDown !== null) return

    s.cancelMediaDown = deps.after(() => {
      s.cancelMediaDown = null
      if (session !== s) return

      tellServerEnd(s, true)
      finish(s, 'Failed', store().phase === 'active' ? 'The call was dropped.' : 'The call could not be connected.')
    }, MEDIA_DOWN_MS)
  }

  // Restarts ICE after `ms` if the media is down or the offer has not been answered, and again every RESTART_RETRY_MS until it is back.
  function armRestart(s: Session, ms: number) {
    s.cancelRestart?.()
    s.cancelRestart = deps.after(() => {
      s.cancelRestart = null
      const down = s.peerState === 'disconnected' || s.peerState === 'failed'
      if (session !== s || s.restarts >= MAX_RESTARTS || (!s.awaitingAnswer && !down)) return

      restart(s)
      armRestart(s, RESTART_RETRY_MS)
    }, ms)
  }

  // A new offer with new candidates, signed and checked like the first one.
  function restart(s: Session) {
    s.restarts++
    enqueue(s, async () => {
      const peer = s.peer
      if (peer === null || session !== s) return

      await sendDescription(s, 'Offer', () => peer.restartOffer(s.iceServers))
    })
  }

  // One job after another, so an offer being answered is finished before a candidate after it is looked at.
  function enqueue(s: Session, job: () => Promise<void>) {
    s.chain = s.chain.then(job).catch((error: unknown) => fail(s, error))
  }

  async function handleSignal(s: Session, type: SignalType, payload: string) {
    if (session !== s || s.peer === null) return

    if (type === 'Candidate') {
      const candidate = decodeCandidate(payload)
      if (candidate !== null) await s.peer.addCandidate(candidate).catch(() => {})
      return
    }

    // An offer is for the callee and an answer for the caller; the other way round is nobody's to act on.
    const role: CallRole | null = type === 'Offer' && !s.outgoing ? 'offer' : type === 'Answer' && s.outgoing ? 'answer' : null
    if (role === null) return

    const signal = decodeSdpSignal(payload)
    if (signal === null) throw new NotVerified()

    const peerKey = await deps.identity.peerIdentity(s.peerId)
    if (!(await verifyCall(peerKey, context(s, 'peer'), role, signal.sdp, signal.signature).catch(() => false))) throw new NotVerified()
    if (session !== s || s.peer === null) return

    const peer = s.peer
    if (role === 'offer') {
      await sendDescription(s, 'Answer', () => peer.acceptOffer(signal.sdp))
    } else {
      await peer.acceptAnswer(signal.sdp)
      s.awaitingAnswer = false
    }
  }

  // ##### Starting and answering #####

  async function start(input: StartCallInput) {
    if (store().phase !== 'idle') return
    cancelClear?.()

    const s: Session = {
      callId: null,
      conversationId: input.conversationId,
      peerId: input.peerId,
      peerName: input.peerName,
      media: input.media,
      outgoing: true,
      token: null,
      iceServers: [],
      peer: null,
      local: null,
      answered: false,
      accepting: false,
      queuedCandidates: null,
      peerState: 'connecting',
      restarts: 0,
      awaitingAnswer: false,
      cancelRestart: null,
      heldSignals: [],
      chain: Promise.resolve(),
      cancelMediaDown: null,
      cancelRingEnd: null,
    }
    session = s
    store().begin(input)

    const abort = (notice: string) => finish(s, null, notice)

    const api = deps.api()
    if (api === null) return abort('You are not connected. Try again in a moment.')

    try {
      await deps.identity.peerIdentity(input.peerId)
    } catch (error) {
      if (session !== s) return
      return abort(
        error instanceof PeerKeysUnavailable
          ? `${input.peerName} has not set up encryption yet, so they cannot be called.`
          : error instanceof PeerIdentityChanged
            ? `The security keys of ${input.peerName} changed. Compare the safety number before calling.`
            : 'The call could not be started.',
      )
    }
    if (session !== s) return

    let local: MediaStream
    try {
      local = await deps.media.capture(input.media === 'Video')
    } catch (error) {
      if (session !== s) return
      return abort(error instanceof MediaUnavailable ? error.message : 'The microphone or camera could not be opened.')
    }
    if (session !== s) return stopStream(local)
    s.local = local
    watchTracks(s, local)

    let ringsUntil: string
    try {
      const started = await api.startCall(input.conversationId, input.media)

      // Hung up while the server was making the call: it exists now, so it is ended.
      if (session !== s) {
        api.endCall(started.callId, false).catch(() => {})
        return stopStream(local)
      }

      s.callId = started.callId
      s.token = started.rejoinToken
      s.iceServers = started.iceServers
      ringsUntil = started.ringsUntil
    } catch (error) {
      if (session !== s) return stopStream(local)
      return abort(startRefusal(callErrorCode(error), input.peerName))
    }

    store().calling(s.callId, ringsUntil)
    store().setStreams({ local })
    deps.ringtone.start('outgoing')
  }

  function incoming(call: IncomingCall) {
    if (store().phase !== 'idle') return
    cancelClear?.()

    const s: Session = {
      callId: call.callId,
      conversationId: call.conversationId,
      peerId: call.callerId,
      peerName: call.callerDisplayName,
      media: call.media,
      outgoing: false,
      token: null,
      iceServers: [],
      peer: null,
      local: null,
      answered: false,
      accepting: false,
      queuedCandidates: null,
      peerState: 'connecting',
      restarts: 0,
      awaitingAnswer: false,
      cancelRestart: null,
      heldSignals: [],
      chain: Promise.resolve(),
      cancelMediaDown: null,
      cancelRingEnd: null,
    }
    session = s

    store().ring({
      callId: call.callId,
      conversationId: call.conversationId,
      peerId: call.callerId,
      peerName: call.callerDisplayName,
      media: call.media,
      ringsUntil: call.ringsUntil,
    })
    deps.ringtone.start('incoming')
    deps.onIncoming?.(call)

    // The server ends a call that nobody answers; if it could not say so (the connection dropped), the screen is not left up for ever.
    const untilEnd = Math.max(0, Date.parse(call.ringsUntil) - deps.now()) + RING_GRACE_MS
    s.cancelRingEnd = deps.after(() => {
      if (session === s && store().phase === 'incoming') finish(s, null, null)
    }, untilEnd)
  }

  async function accept(withVideo = true) {
    const s = session
    if (s === null || store().phase !== 'incoming' || s.callId === null) return

    const api = deps.api()
    if (api === null) return store().setNotice('You are not connected. Try again in a moment.')

    s.accepting = true

    let local: MediaStream
    try {
      local = await deps.media.capture(withVideo && s.media === 'Video')
    } catch (error) {
      // Still ringing: the person can allow the microphone and answer, or decline.
      s.accepting = false
      return store().setNotice(error instanceof MediaUnavailable ? error.message : 'The microphone or camera could not be opened.')
    }
    if (session !== s || store().phase !== 'incoming') return stopStream(local)

    try {
      const accepted = await api.acceptCall(s.callId)
      s.token = accepted.rejoinToken
      s.iceServers = accepted.iceServers
    } catch (error) {
      stopStream(local)
      if (session !== s) return

      const code = callErrorCode(error)
      return finishAfterRefusal(s, code === 'call-not-ringing' || code === 'call-not-found' ? 'This call was answered on another device or has ended.' : 'The call could not be answered.')
    }
    if (session !== s) {
      stopStream(local)
      return
    }

    s.local = local
    watchTracks(s, local)
    s.answered = true
    s.cancelRingEnd?.()
    s.cancelRingEnd = null
    deps.ringtone.stop()
    store().answered(deps.now(), local.getVideoTracks().length > 0)
    store().setStreams({ local })
    connect(s)

    // The offer can reach this tab before the server has answered its AcceptCall.
    for (const held of s.heldSignals.splice(0)) enqueue(s, () => handleSignal(s, held.type, held.payload))
  }

  // The answer was refused: the ring screen says why and goes.
  function finishAfterRefusal(s: Session, notice: string) {
    teardown(s)
    session = null
    store().finish(null, notice)
    scheduleClear()
  }

  // ##### Events from the hub #####

  function onCallAnswered(callId: string) {
    const s = session
    if (s === null || s.callId !== callId) return

    const phase = store().phase

    if (s.outgoing && phase === 'outgoing') {
      deps.ringtone.stop()
      s.answered = true
      store().answered(deps.now())
      connect(s)

      enqueue(s, async () => {
        const peer = s.peer
        if (peer === null || session !== s) return

        await sendDescription(s, 'Offer', () => peer.createOffer())
      })
      return
    }

    // Another tab of this person answered while this one was still ringing. (The server tells the tab that answered too, and that one is
    // waiting for its own answer to come back, possibly after this.)
    if (!s.outgoing && phase === 'incoming' && !s.accepting) finish(s, null, null)
  }

  function onCallSignal(callId: string, type: SignalType, payload: string) {
    const s = session
    if (s === null || s.callId !== callId) return

    // The callee's connection does not exist until it has answered; the offer can come first.
    if (s.peer === null) {
      s.heldSignals.push({ type, payload })
      return
    }

    enqueue(s, () => handleSignal(s, type, payload))
  }

  function onCallEnded(ended: CallEnded) {
    const s = session
    if (s === null || s.callId !== ended.callId) return

    const phase = store().phase
    const answered = s.answered || phase === 'connecting' || phase === 'active'

    teardown(s)
    session = null

    // A call that was only ringing here (missed, cancelled, declined elsewhere) goes without a screen.
    if (phase === 'incoming') {
      store().clear()
      return
    }

    store().finish(ended.reason, endedNotice({ reason: ended.reason, outgoing: s.outgoing, answered, peerName: s.peerName }))
    scheduleClear()
  }

  // The hub connection came back: a side that was running takes the call back with its token. If the server says the call is gone, so is it here.
  function onReconnected() {
    const s = session
    const phase = store().phase
    if (s === null || s.callId === null || s.token === null) return
    if (phase !== 'outgoing' && phase !== 'connecting' && phase !== 'active') return

    const api = deps.api()
    if (api === null) return

    api.rejoinCall(s.callId, s.token).then(
      (rejoined) => {
        if (session !== s) return
        s.iceServers = rejoined.iceServers

        // What was signalled while this side was away did not arrive. A call whose media is not up is offered again (the caller drives) with the
        // servers just given; one whose media stayed up through the drop needs nothing.
        if (s.outgoing && s.peer !== null && s.peerState !== 'connected') restart(s)
      },
      (error: unknown) => {
        const code = callErrorCode(error)
        if (session === s && (code === 'call-not-found' || code === 'call-ended' || code === 'not-in-the-call' || code === 'bad-token')) {
          finish(s, 'Failed', 'The call ended while you were offline.')
        }
      },
    )
  }

  // ##### Actions of the person #####

  function end() {
    const s = session

    if (s === null) {
      store().clear()
      return
    }

    tellServerEnd(s, false)
    finish(s, null, null)
  }

  function setMuted(muted: boolean) {
    session?.local?.getAudioTracks().forEach((track) => (track.enabled = !muted))
    store().setMuted(muted)
  }

  function setCameraOff(off: boolean) {
    session?.local?.getVideoTracks().forEach((track) => (track.enabled = !off))
    store().setCameraOff(off)
  }

  async function switchDevice(kind: 'audio' | 'video', deviceId: string) {
    const s = session
    if (s === null || s.local === null || s.peer === null) return

    try {
      const track = await deps.media.captureDevice(kind, deviceId)
      if (session !== s || s.local === null || s.peer === null) return track.stop()

      await swapTrack(s, kind, track)
    } catch (error) {
      store().setNotice(error instanceof MediaUnavailable ? error.message : 'The device could not be used.')
    }
  }

  // Sends this track instead of the one of its kind. The new track keeps what the person chose: a muted microphone stays muted.
  async function swapTrack(s: Session, kind: 'audio' | 'video', track: MediaStreamTrack) {
    const local = s.local as MediaStream

    track.enabled = kind === 'audio' ? !store().muted : !store().cameraOff
    await s.peer?.replaceTrack(kind, track)

    const previous = kind === 'audio' ? local.getAudioTracks() : local.getVideoTracks()
    previous.forEach((old) => {
      local.removeTrack(old)
      old.stop()
    })
    local.addTrack(track)
    watchTracks(s, track)
    store().setStreams({ local })
  }

  // ##### A device that goes away #####

  // A track that ends by itself (a headset unplugged, a camera taken by another program) is replaced by the default device of its kind. The
  // tracks this side stops, when it switches device or the call ends, do not raise `ended`, and are no longer in the stream anyway.
  function watchTracks(s: Session, source: MediaStream | MediaStreamTrack) {
    const tracks = 'getTracks' in source ? source.getTracks() : [source]
    for (const track of tracks) track.addEventListener('ended', () => void lost(s, track))
  }

  async function lost(s: Session, track: MediaStreamTrack) {
    if (session !== s || s.local === null || !s.local.getTracks().includes(track)) return

    const kind = track.kind as 'audio' | 'video'

    try {
      const replacement = await deps.media.captureDefault(kind)
      if (session !== s || s.local === null) return replacement.stop()

      await swapTrack(s, kind, replacement)
      store().setNotice(deviceLostNotice(kind, true))
    } catch {
      if (session !== s) return
      if (kind === 'video') setCameraOff(true)
      store().setNotice(deviceLostNotice(kind, false))
    }
  }

  function leave() {
    const phase = store().phase
    if (phase === 'outgoing' || phase === 'connecting' || phase === 'active') end()
  }

  function dispose() {
    cancelClear?.()
    cancelClear = null
    if (session !== null) teardown(session)
    session = null
    store().clear()
  }

  return {
    start,
    accept,
    end,
    setMuted,
    setCameraOff,
    switchDevice,
    leave,
    dispose,
    onIncomingCall: incoming,
    onCallAnswered,
    onCallSignal,
    onCallEnded,
    onReconnected,
  }
}
