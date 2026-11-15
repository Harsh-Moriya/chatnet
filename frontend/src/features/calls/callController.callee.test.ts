import { beforeEach, describe, expect, it } from 'vitest'
import { verifyCall } from '../../lib/crypto/call'
import { useCallStore } from './callStore'
import { NOT_VERIFIED } from './callTexts'
import {
  ANSWER_SDP,
  CALL,
  CONVERSATION,
  OFFER_SDP,
  OTHER,
  SELF,
  createRig,
  incomingCall,
  refusal,
  settle,
  signedByOther,
  type Rig,
} from './callTestKit'
import { MediaUnavailable } from './media'
import { decodeSdpSignal, encodeCandidate } from './signals'
import type { CallAccepted } from './types'

// The callee's side: it rings, opens the microphone before it tells the server it answers, builds its connection when it has answered, checks
// the offer's signature before using it, and signs its answer. An offer can arrive before the server has answered the AcceptCall.

let rig: Rig
const store = () => useCallStore.getState()

beforeEach(async () => {
  rig = await createRig()
})

const stopped = () => rig.media.streams.every((stream) => stream.tracks.every((track) => track.stopped))
const accepted = async (withVideo = true) => {
  rig.controller.onIncomingCall(incomingCall())
  await rig.controller.accept(withVideo)
  await settle()
}
const candidate = (n: number) => encodeCandidate({ candidate: `candidate:${n}`, sdpMid: '0', sdpMLineIndex: 0, usernameFragment: null })

describe('a call coming in', () => {
  it('rings, tells whoever wants to alert the person, and counts down to the end of the ring', () => {
    rig.controller.onIncomingCall(incomingCall())

    expect(store()).toMatchObject({ phase: 'incoming', callId: CALL, peerId: OTHER, peerName: 'Ben Caller', outgoing: false, media: 'Audio' })
    expect(rig.ringtone.ringing).toBe('incoming')
    expect(rig.incoming).toEqual([incomingCall()])
    // 30 seconds of ringing and a few more, after which a screen nobody took down goes by itself.
    expect(rig.timers.waiting((ms) => ms === 33_000)).toBe(1)
  })

  it('does not ring for a second call while one is ringing or in progress', async () => {
    rig.controller.onIncomingCall(incomingCall())
    rig.controller.onIncomingCall(incomingCall({ callId: 'ca11ca11-0000-4000-8000-0000000000ff', callerDisplayName: 'Intruder' }))

    expect(store().peerName).toBe('Ben Caller')
    expect(rig.incoming).toHaveLength(1)
  })

  it('takes down a ring screen that the server never ended', () => {
    rig.controller.onIncomingCall(incomingCall())

    rig.timers.fire((ms) => ms === 33_000)

    expect(store().phase).toBe('idle')
    expect(rig.ringtone.events.at(-1)).toBe('stop')
  })

  it.each(['Missed', 'Cancelled'] as const)('goes without a screen when it ends as %s before it was answered', (reason) => {
    rig.controller.onIncomingCall(incomingCall())

    rig.controller.onCallEnded({ callId: CALL, reason, endedAt: '2030-01-01T00:00:05Z' })

    expect(store().phase).toBe('idle')
    expect(rig.ringtone.events.at(-1)).toBe('stop')
  })

  it('stops ringing when another tab of the person answered', () => {
    rig.controller.onIncomingCall(incomingCall())

    rig.controller.onCallAnswered(CALL)

    expect(store().phase).toBe('idle')
    expect(rig.ringtone.events.at(-1)).toBe('stop')
    expect(rig.api.calls).toEqual([])
  })

  it('is declined by the person, and told to the server, without a screen about it', () => {
    rig.controller.onIncomingCall(incomingCall())

    rig.controller.end()

    expect(rig.api.ended).toEqual([{ callId: CALL, failed: false }])
    expect(store().phase).toBe('idle')
    expect(rig.ringtone.events.at(-1)).toBe('stop')
  })
})

describe('answering', () => {
  it('opens the microphone first, then tells the server, then makes the connection', async () => {
    rig.controller.onIncomingCall(incomingCall())
    rig.api.acceptResult = async () => {
      expect(rig.media.captures).toEqual([false])
      return { rejoinToken: 'callee-token', media: 'Audio', callerId: OTHER, iceServers: [] }
    }

    await rig.controller.accept()
    await settle()

    expect(rig.api.calls).toContain(`acceptCall:${CALL}`)
    expect(store()).toMatchObject({ phase: 'connecting', outgoing: false })
    expect(rig.ringtone.events.at(-1)).toBe('stop')
    expect(rig.peers.current.tracks.map((t) => t.kind)).toEqual(['audio'])
    expect(store().localStream).toBe(rig.media.streams[0])
  })

  it('opens the camera for a video call, or only the microphone when the person answers as audio', async () => {
    rig.controller.onIncomingCall(incomingCall({ media: 'Video' }))
    await rig.controller.accept(true)
    await settle()
    expect(rig.media.captures).toEqual([true])
    expect(store().sendingVideo).toBe(true)

    rig.controller.dispose()
    rig.controller.onIncomingCall(incomingCall({ media: 'Video' }))
    await rig.controller.accept(false)
    await settle()
    expect(rig.media.captures).toEqual([true, false])
    expect(store().sendingVideo).toBe(false)
  })

  it('does not answer, and keeps ringing, when the microphone cannot be opened, and the person can still decline', async () => {
    rig.controller.onIncomingCall(incomingCall())
    rig.media.failWith = new MediaUnavailable('denied')

    await rig.controller.accept()

    expect(rig.api.calls).toEqual([])
    expect(store().phase).toBe('incoming')
    expect(store().notice).toContain('Allow the microphone')
    expect(rig.ringtone.ringing).toBe('incoming')

    rig.controller.end()
    expect(rig.api.ended).toEqual([{ callId: CALL, failed: false }])
  })

  it.each(['call-not-ringing', 'call-not-found'])('lets go of the microphone and says the call was answered elsewhere or ended, on %s', async (code) => {
    rig.controller.onIncomingCall(incomingCall())
    rig.api.acceptResult = async () => {
      throw refusal(code)
    }

    await rig.controller.accept()

    expect(store()).toMatchObject({ phase: 'ended', notice: 'This call was answered on another device or has ended.' })
    expect(stopped()).toBe(true)
    expect(rig.peers.peers).toHaveLength(0)
  })

  it('says it could not be answered for any other refusal', async () => {
    rig.controller.onIncomingCall(incomingCall())
    rig.api.acceptResult = async () => {
      throw refusal('calls-unavailable')
    }

    await rig.controller.accept()

    expect(store().notice).toBe('The call could not be answered.')
  })

  it('goes on when the news that the call was answered is its own, which can arrive before the server’s answer', async () => {
    rig.controller.onIncomingCall(incomingCall())
    let release!: (value: CallAccepted) => void
    rig.api.acceptResult = () => new Promise((resolve) => (release = resolve))

    const answering = rig.controller.accept()
    await settle()
    rig.controller.onCallAnswered(CALL)
    release({ rejoinToken: 'callee-token', media: 'Audio', callerId: OTHER, iceServers: [] })
    await answering
    await settle()

    expect(store().phase).toBe('connecting')
    expect(rig.peers.peers).toHaveLength(1)
    expect(stopped()).toBe(false)
  })

  it('does nothing when there is no call ringing', async () => {
    await rig.controller.accept()

    expect(rig.api.calls).toEqual([])
    expect(rig.media.captures).toEqual([])
  })
})

describe('the offer', () => {
  it('is answered with a signed answer when it is signed by the caller for this call', async () => {
    await accepted()

    rig.controller.onCallSignal(CALL, 'Offer', await signedByOther(rig, 'offer', OFFER_SDP))
    await settle()

    expect(rig.peers.current.acceptedOffers).toEqual([OFFER_SDP])
    const [answer] = rig.api.signals
    expect(answer).toMatchObject({ callId: CALL, type: 'Answer' })
    const signal = decodeSdpSignal(answer.payload)!
    expect(signal.sdp).toBe(ANSWER_SDP)
    const context = { callId: CALL, conversationId: CONVERSATION, signerId: SELF, peerId: OTHER }
    expect(await verifyCall(rig.identity.keys.publicKey, context, 'answer', signal.sdp, signal.signature)).toBe(true)
  })

  it('is held when it arrives before the server has answered the AcceptCall, and answered once the connection exists', async () => {
    rig.controller.onIncomingCall(incomingCall())
    let release!: (value: CallAccepted) => void
    rig.api.acceptResult = () => new Promise((resolve) => (release = resolve))
    const answering = rig.controller.accept()
    await settle()

    rig.controller.onCallSignal(CALL, 'Offer', await signedByOther(rig, 'offer', OFFER_SDP))
    await settle()
    expect(rig.peers.peers).toHaveLength(0)

    release({ rejoinToken: 'callee-token', media: 'Audio', callerId: OTHER, iceServers: [] })
    await answering
    await settle()

    expect(rig.peers.current.acceptedOffers).toEqual([OFFER_SDP])
    expect(rig.api.signals.map((s) => s.type)).toEqual(['Answer'])
  })

  it('has the candidates that came first added after it, in order, and the ones after it at once', async () => {
    await accepted()

    rig.controller.onCallSignal(CALL, 'Candidate', candidate(1))
    rig.controller.onCallSignal(CALL, 'Offer', await signedByOther(rig, 'offer', OFFER_SDP))
    rig.controller.onCallSignal(CALL, 'Candidate', candidate(2))
    await settle()

    expect(rig.peers.current.log).toEqual(['acceptOffer', 'candidate candidate:1', 'candidate candidate:2'])
  })

  it('is ignored when it comes from a call that is not this one', async () => {
    await accepted()

    rig.controller.onCallSignal('ca11ca11-0000-4000-8000-0000000000ff', 'Offer', await signedByOther(rig, 'offer', OFFER_SDP))
    await settle()

    expect(rig.peers.current.acceptedOffers).toEqual([])
  })

  it('is ignored when it is an answer, which is not for the callee', async () => {
    await accepted()

    rig.controller.onCallSignal(CALL, 'Answer', await signedByOther(rig, 'answer', ANSWER_SDP))
    await settle()

    expect(rig.peers.current.acceptedAnswers).toEqual([])
    expect(store().phase).toBe('connecting')
  })

  describe('is not used, and the call ends as failed, when', () => {
    const cases: [string, (rig: Rig) => Promise<string>][] = [
      ['it is signed by a key that is not the caller’s', async (r) => signedByOther(r, 'offer', OFFER_SDP, { key: r.identity.keys })],
      [
        'its description was changed after it was signed',
        async (r) => JSON.stringify({ ...JSON.parse(await signedByOther(r, 'offer', OFFER_SDP)), sdp: OFFER_SDP.replace('11:11', '99:99') }),
      ],
      ['it was signed for another call', async (r) => signedByOther(r, 'offer', OFFER_SDP, { callId: 'ca11ca11-0000-4000-8000-0000000000ff' })],
      ['it was signed as an answer', async (r) => signedByOther(r, 'answer', OFFER_SDP)],
      ['it carries no signature', async () => JSON.stringify({ sdp: OFFER_SDP })],
    ]

    it.each(cases)('%s', async (_name, make) => {
      await accepted()

      rig.controller.onCallSignal(CALL, 'Offer', await make(rig))
      await settle()

      expect(rig.peers.current.acceptedOffers).toEqual([])
      expect(rig.api.signals).toEqual([])
      expect(rig.api.ended).toEqual([{ callId: CALL, failed: true }])
      expect(stopped()).toBe(true)
      expect(store()).toMatchObject({ phase: 'ended', endedReason: 'Failed', notice: NOT_VERIFIED })
    })
  })
})

describe('the call going on and ending', () => {
  it('is active once the media connects', async () => {
    await accepted()

    rig.peers.current.handlers.onState('connected')

    expect(store().phase).toBe('active')
  })

  it('ends with the server’s news that the caller hung up, and lets go of everything', async () => {
    await accepted()
    rig.peers.current.handlers.onState('connected')

    rig.controller.onCallEnded({ callId: CALL, reason: 'Completed', endedAt: '2030-01-01T00:04:00Z' })

    expect(store()).toMatchObject({ phase: 'ended', endedReason: 'Completed', notice: null })
    expect(rig.peers.current.closed).toBe(true)
    expect(stopped()).toBe(true)
  })

  it('takes the call back with its token after the hub connection came back', async () => {
    await accepted()

    rig.controller.onReconnected()
    await settle()

    expect(rig.api.rejoined).toEqual([{ callId: CALL, token: 'callee-token' }])
  })

  it('does not rejoin a call that is only ringing, because the server rings the new connection itself', async () => {
    rig.controller.onIncomingCall(incomingCall())

    rig.controller.onReconnected()
    await settle()

    expect(rig.api.rejoined).toEqual([])
    expect(store().phase).toBe('incoming')
  })

  it('hangs up, tells the server, and shows that it ended', async () => {
    await accepted()

    rig.controller.end()

    expect(rig.api.ended).toEqual([{ callId: CALL, failed: false }])
    expect(store()).toMatchObject({ phase: 'ended', notice: null })
    expect(stopped()).toBe(true)
  })
})
