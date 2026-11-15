import { beforeEach, describe, expect, it } from 'vitest'
import { verifyCall } from '../../lib/crypto/call'
import { ENDED_SHOWN_MS, MEDIA_DOWN_MS } from './callController'
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
  identityChanged,
  keysUnavailable,
  refusal,
  settle,
  signedByOther,
  type Rig,
} from './callTestKit'
import { MediaUnavailable } from './media'
import { decodeCandidate, decodeSdpSignal, encodeCandidate } from './signals'
import type { CallStarted } from './types'

// The caller's side: it checks keys and opens the microphone before anything rings, makes its offer only after the call is answered, signs it,
// checks the answer's signature, and cleans up whatever way the call ends.

let rig: Rig
const store = () => useCallStore.getState()
const input = { conversationId: CONVERSATION, peerId: OTHER, peerName: 'Ben Callee', media: 'Audio' as const }

beforeEach(async () => {
  rig = await createRig()
})

const ringing = async (media: 'Audio' | 'Video' = 'Audio') => {
  await rig.controller.start({ ...input, media })
}

const answered = async () => {
  await ringing()
  rig.controller.onCallAnswered(CALL)
  await settle()
}

const stopped = () => rig.media.streams.every((stream) => stream.tracks.every((track) => track.stopped))

describe('starting a call', () => {
  it('checks the other person’s keys, opens the microphone, and only then asks the server, and rings out', async () => {
    await ringing()

    expect(rig.media.log).toEqual(['capture'])
    expect(rig.media.captures).toEqual([false])
    expect(rig.api.started).toEqual([{ conversationId: CONVERSATION, media: 'Audio' }])
    expect(store()).toMatchObject({ phase: 'outgoing', callId: CALL, ringsUntil: '2030-01-01T00:00:30Z', outgoing: true, peerName: 'Ben Callee' })
    expect(rig.ringtone.ringing).toBe('outgoing')
    expect(store().localStream).toBe(rig.media.streams[0])
  })

  it('opens the camera too for a video call', async () => {
    await ringing('Video')

    expect(rig.media.captures).toEqual([true])
    expect(store().sendingVideo).toBe(true)
  })

  it('makes no connection and no offer yet: nothing is captured or sent to somebody who has not answered', async () => {
    await ringing()

    expect(rig.peers.peers).toHaveLength(0)
    expect(rig.api.signals).toHaveLength(0)
  })

  it('does nothing while another call is in progress', async () => {
    await ringing()
    await rig.controller.start({ ...input, peerName: 'Somebody else' })

    expect(rig.api.started).toHaveLength(1)
    expect(store().peerName).toBe('Ben Callee')
  })

  it.each([
    ['denied', 'Allow the microphone'],
    ['missing', 'No microphone was found'],
    ['busy', 'in use'],
  ] as const)('starts no call when the microphone is %s, and says so', async (failure, words) => {
    rig.media.failWith = new MediaUnavailable(failure)

    await ringing()

    expect(rig.api.calls).toEqual([])
    expect(store()).toMatchObject({ phase: 'ended' })
    expect(store().notice).toContain(words)
    expect(rig.ringtone.events).not.toContain('start:outgoing')
  })

  it('starts no call for a person who has not set up encryption', async () => {
    rig.identity.failWith = keysUnavailable()

    await ringing()

    expect(rig.media.captures).toEqual([])
    expect(rig.api.calls).toEqual([])
    expect(store().notice).toBe('Ben Callee has not set up encryption yet, so they cannot be called.')
  })

  it('starts no call for a person whose keys changed, and says to compare the safety number', async () => {
    rig.identity.failWith = identityChanged()

    await ringing()

    expect(rig.api.calls).toEqual([])
    expect(store().notice).toContain('Compare the safety number before calling')
  })

  it.each([
    ['callee-busy', 'Ben Callee is in another call.'],
    ['already-in-a-call', 'You are already in a call.'],
    ['rate-limited', 'You are calling too often. Try again in a minute.'],
  ])('lets go of the microphone and says why when the server refuses with %s', async (code, words) => {
    rig.api.startResult = async () => {
      throw refusal(code)
    }

    await ringing()

    expect(store()).toMatchObject({ phase: 'ended', notice: words })
    expect(stopped()).toBe(true)
    expect(rig.ringtone.events).not.toContain('start:outgoing')
  })

  it('takes the call back at once when the person hangs up while the server is still making it', async () => {
    let release!: (started: CallStarted) => void
    rig.api.startResult = () => new Promise((resolve) => (release = resolve))
    const starting = rig.controller.start(input)
    await settle()

    rig.controller.end()
    release({ callId: CALL, rejoinToken: 't', ringsUntil: 'x', iceServers: [] })
    await starting

    expect(rig.api.ended).toEqual([{ callId: CALL, failed: false }])
    expect(stopped()).toBe(true)
    expect(rig.ringtone.events).not.toContain('start:outgoing')
  })
})

describe('when the call is answered', () => {
  it('makes the connection, adds the microphone, and sends a signed offer', async () => {
    await answered()

    expect(store().phase).toBe('connecting')
    expect(rig.ringtone.events.at(-1)).toBe('stop')
    expect(rig.peers.current.tracks.map((t) => t.kind)).toEqual(['audio'])
    expect(rig.peers.current.offersMade).toBe(1)

    const [offer] = rig.api.signals
    expect(offer).toMatchObject({ callId: CALL, type: 'Offer' })
    const signal = decodeSdpSignal(offer.payload)!
    expect(signal.sdp).toBe(OFFER_SDP)
    const context = { callId: CALL, conversationId: CONVERSATION, signerId: SELF, peerId: OTHER }
    expect(await verifyCall(rig.identity.keys.publicKey, context, 'offer', signal.sdp, signal.signature)).toBe(true)
  })

  it('does not make a second offer if the news comes twice', async () => {
    await answered()
    rig.controller.onCallAnswered(CALL)
    await settle()

    expect(rig.peers.peers).toHaveLength(1)
    expect(rig.api.signals.filter((s) => s.type === 'Offer')).toHaveLength(1)
  })

  it('ignores news of another call', async () => {
    await ringing()

    rig.controller.onCallAnswered('ca11ca11-0000-4000-8000-0000000000ff')
    await settle()

    expect(store().phase).toBe('outgoing')
    expect(rig.peers.peers).toHaveLength(0)
  })

  it('sends its own candidates as they are found', async () => {
    await answered()
    const candidate = { candidate: 'candidate:1 1 udp 1 10.0.0.1 5000 typ host', sdpMid: '0', sdpMLineIndex: 0, usernameFragment: 'f' }

    rig.peers.current.handlers.onCandidate(candidate)

    const sent = rig.api.signals.filter((s) => s.type === 'Candidate')
    expect(sent).toHaveLength(1)
    expect(decodeCandidate(sent[0].payload)).toEqual(candidate)
  })

  it('shows what the other side sends as the remote picture', async () => {
    await answered()
    const remote = {} as MediaStream

    rig.peers.current.handlers.onTrack(remote)

    expect(store().remoteStream).toBe(remote)
  })
})

describe('the answer', () => {
  it('is accepted when it is signed by the other person for this call, and the connection is made', async () => {
    await answered()

    rig.controller.onCallSignal(CALL, 'Answer', await signedByOther(rig, 'answer', ANSWER_SDP))
    await settle()

    expect(rig.peers.current.acceptedAnswers).toEqual([ANSWER_SDP])
    expect(store().phase).toBe('connecting')
  })

  it('holds candidates that arrive before it, and adds them once the connection has it', async () => {
    await answered()
    const candidate = (n: number) => encodeCandidate({ candidate: `candidate:${n}`, sdpMid: '0', sdpMLineIndex: 0, usernameFragment: null })

    rig.controller.onCallSignal(CALL, 'Candidate', candidate(1))
    rig.controller.onCallSignal(CALL, 'Answer', await signedByOther(rig, 'answer', ANSWER_SDP))
    rig.controller.onCallSignal(CALL, 'Candidate', candidate(2))
    await settle()

    expect(rig.peers.current.log).toEqual(['createOffer', 'acceptAnswer', 'candidate candidate:1', 'candidate candidate:2'])
  })

  it('adds no candidate before it has the description, however many come', async () => {
    await answered()

    rig.controller.onCallSignal(CALL, 'Candidate', encodeCandidate({ candidate: 'candidate:1', sdpMid: null, sdpMLineIndex: null, usernameFragment: null }))
    await settle()

    expect(rig.peers.current.candidates).toEqual([])
  })

  it('ignores an offer, which is not for the caller, and a candidate that is not one', async () => {
    await answered()

    rig.controller.onCallSignal(CALL, 'Offer', await signedByOther(rig, 'offer', OFFER_SDP))
    rig.controller.onCallSignal(CALL, 'Candidate', 'not a candidate')
    await settle()

    expect(rig.peers.current.acceptedOffers).toEqual([])
    expect(store().phase).toBe('connecting')
    expect(rig.api.ended).toEqual([])
  })

  describe('is not accepted, and ends the call as failed, when', () => {
    const cases: [string, (rig: Rig) => Promise<string>][] = [
      ['it is signed by a key that is not the other person’s', async (r) => signedByOther(r, 'answer', ANSWER_SDP, { key: r.identity.keys })],
      [
        'its description was changed after it was signed',
        async (r) => JSON.stringify({ ...JSON.parse(await signedByOther(r, 'answer', ANSWER_SDP)), sdp: ANSWER_SDP.replace('22:22', '33:33') }),
      ],
      ['it was signed for another call', async (r) => signedByOther(r, 'answer', ANSWER_SDP, { callId: 'ca11ca11-0000-4000-8000-0000000000ff' })],
      ['it was signed as an offer', async (r) => signedByOther(r, 'offer', ANSWER_SDP)],
      ['it is not a signal at all', async () => 'garbage'],
      ['its description names no fingerprint', async (r) => signedByOther(r, 'answer', 'v=0\r\n').catch(() => JSON.stringify({ sdp: 'v=0\r\n', signature: 'AAAA' }))],
    ]

    it.each(cases)('%s', async (_name, make) => {
      await answered()

      rig.controller.onCallSignal(CALL, 'Answer', await make(rig))
      await settle()

      expect(rig.peers.current.acceptedAnswers).toEqual([])
      expect(rig.api.ended).toEqual([{ callId: CALL, failed: true }])
      expect(rig.peers.current.closed).toBe(true)
      expect(stopped()).toBe(true)
      expect(store()).toMatchObject({ phase: 'ended', endedReason: 'Failed', notice: NOT_VERIFIED })
    })

    it('the other person’s keys changed since the call began, with the notice that says so', async () => {
      await answered()
      rig.identity.failWith = identityChanged()

      rig.controller.onCallSignal(CALL, 'Answer', await signedByOther(rig, 'answer', ANSWER_SDP))
      await settle()

      expect(rig.api.ended).toEqual([{ callId: CALL, failed: true }])
      expect(store().notice).toBe('The security keys of Ben Callee changed. Compare the safety number, then call again.')
    })

    it('the other person has no keys any more', async () => {
      await answered()
      rig.identity.failWith = keysUnavailable()

      rig.controller.onCallSignal(CALL, 'Answer', await signedByOther(rig, 'answer', ANSWER_SDP))
      await settle()

      expect(store().notice).toBe(NOT_VERIFIED)
    })
  })
})

describe('the media', () => {
  it('connects when the browser says so', async () => {
    await answered()

    rig.peers.current.handlers.onState('connected')

    expect(store().phase).toBe('active')
  })

  it('is given the time to come back when it drops, and the call is ended as dropped when it does not', async () => {
    await answered()
    rig.peers.current.handlers.onState('connected')

    rig.peers.current.handlers.onState('disconnected')
    expect(rig.timers.waiting((ms) => ms === MEDIA_DOWN_MS)).toBe(1)
    rig.timers.fire((ms) => ms === MEDIA_DOWN_MS)

    expect(rig.api.ended).toEqual([{ callId: CALL, failed: true }])
    expect(store()).toMatchObject({ phase: 'ended', notice: 'The call was dropped.' })
    expect(stopped()).toBe(true)
  })

  it('goes on when it comes back inside that time', async () => {
    await answered()
    rig.peers.current.handlers.onState('connected')

    rig.peers.current.handlers.onState('disconnected')
    rig.peers.current.handlers.onState('connected')
    rig.timers.fire((ms) => ms === MEDIA_DOWN_MS)

    expect(store().phase).toBe('active')
    expect(rig.api.ended).toEqual([])
  })

  it('starts one wait however many times it drops', async () => {
    await answered()

    rig.peers.current.handlers.onState('disconnected')
    rig.peers.current.handlers.onState('failed')

    expect(rig.timers.waiting((ms) => ms === MEDIA_DOWN_MS)).toBe(1)
  })

  it('mutes and turns the camera off by switching the tracks, and says so', async () => {
    await ringing('Video')

    rig.controller.setMuted(true)
    rig.controller.setCameraOff(true)

    const [stream] = rig.media.streams
    expect(stream.getAudioTracks()[0].enabled).toBe(false)
    expect(stream.getVideoTracks()[0].enabled).toBe(false)
    expect(store()).toMatchObject({ muted: true, cameraOff: true })

    rig.controller.setMuted(false)
    expect(stream.getAudioTracks()[0].enabled).toBe(true)
  })

  it('sends a chosen microphone instead, stops the old one, and keeps it muted if it was', async () => {
    await answered()
    rig.controller.setMuted(true)

    await rig.controller.switchDevice('audio', 'headset')

    const [device] = rig.media.devices
    expect(device).toMatchObject({ kind: 'audio', deviceId: 'headset' })
    expect(device.track.enabled).toBe(false)
    expect(rig.peers.current.replaced).toEqual([{ kind: 'audio', track: device.track }])
    const [stream] = rig.media.streams
    expect(stream.tracks).toEqual([device.track])
  })

  it('says so when a chosen device cannot be opened, and goes on with the one it had', async () => {
    await answered()
    rig.media.failWith = new MediaUnavailable('busy')

    await rig.controller.switchDevice('video', 'camera-2')

    expect(store().notice).toContain('in use')
    expect(rig.peers.current.replaced).toEqual([])
  })
})

describe('when the call ends', () => {
  it('on the person’s hang-up, it tells the server, lets go of everything and shows that it ended', async () => {
    await answered()

    rig.controller.end()

    expect(rig.api.ended).toEqual([{ callId: CALL, failed: false }])
    expect(rig.peers.current.closed).toBe(true)
    expect(stopped()).toBe(true)
    expect(store()).toMatchObject({ phase: 'ended', notice: null })
    expect(store().localStream).toBeNull()
  })

  it('on a cancel while it rings, stops the ring and does not act on the news that follows', async () => {
    await ringing()

    rig.controller.end()
    rig.controller.onCallEnded({ callId: CALL, reason: 'Cancelled', endedAt: '2030-01-01T00:00:05Z' })

    expect(rig.ringtone.events.at(-1)).toBe('stop')
    expect(rig.api.ended).toEqual([{ callId: CALL, failed: false }])
    expect(store().phase).toBe('ended')
  })

  it.each([
    ['Declined', 'Ben Callee declined the call.'],
    ['Missed', 'Ben Callee did not answer.'],
  ] as const)('on the server’s news that it was %s, says so', async (reason, words) => {
    await ringing()

    rig.controller.onCallEnded({ callId: CALL, reason, endedAt: '2030-01-01T00:00:30Z' })

    expect(store()).toMatchObject({ phase: 'ended', endedReason: reason, notice: words })
    expect(rig.ringtone.events.at(-1)).toBe('stop')
    expect(stopped()).toBe(true)
  })

  it('on the server’s news that the other person hung up, ends with nothing more to say', async () => {
    await answered()

    rig.controller.onCallEnded({ callId: CALL, reason: 'Completed', endedAt: '2030-01-01T00:04:00Z' })

    expect(store()).toMatchObject({ phase: 'ended', endedReason: 'Completed', notice: null })
    expect(rig.peers.current.closed).toBe(true)
  })

  it('ignores the news of a call that is not this one', async () => {
    await ringing()

    rig.controller.onCallEnded({ callId: 'ca11ca11-0000-4000-8000-0000000000ff', reason: 'Missed', endedAt: 'x' })

    expect(store().phase).toBe('outgoing')
  })

  it('goes from the screen that says how it ended after a few seconds, and not a call that began in between', async () => {
    await ringing()
    rig.controller.onCallEnded({ callId: CALL, reason: 'Declined', endedAt: 'x' })

    rig.timers.fire((ms) => ms === ENDED_SHOWN_MS)

    expect(store().phase).toBe('idle')
  })

  it('lets a new call be started once the old one is cleared, and the old timer does not clear the new call', async () => {
    await ringing()
    rig.controller.onCallEnded({ callId: CALL, reason: 'Declined', endedAt: 'x' })
    rig.controller.end()
    expect(store().phase).toBe('idle')

    await ringing()
    rig.timers.fire((ms) => ms === ENDED_SHOWN_MS)

    expect(store().phase).toBe('outgoing')
  })

  it('is forgotten without a word to anyone when the page goes away', async () => {
    await answered()

    rig.controller.dispose()

    expect(rig.api.ended).toEqual([])
    expect(rig.peers.current.closed).toBe(true)
    expect(stopped()).toBe(true)
    expect(store().phase).toBe('idle')
  })
})

describe('after the hub connection came back', () => {
  it('takes the call back with its token', async () => {
    await answered()

    rig.controller.onReconnected()
    await settle()

    expect(rig.api.rejoined).toEqual([{ callId: CALL, token: 'caller-token' }])
    expect(store().phase).toBe('connecting')
  })

  it.each(['call-not-found', 'call-ended', 'not-in-the-call', 'bad-token'])('ends the call here when the server says %s', async (code) => {
    await answered()
    rig.api.rejoinResult = async () => {
      throw refusal(code)
    }

    rig.controller.onReconnected()
    await settle()

    expect(store()).toMatchObject({ phase: 'ended', notice: 'The call ended while you were offline.' })
    expect(stopped()).toBe(true)
  })

  it('goes on when the server could not be reached for the rejoin', async () => {
    await answered()
    rig.api.rejoinResult = async () => {
      throw refusal('calls-unavailable')
    }

    rig.controller.onReconnected()
    await settle()

    expect(store().phase).toBe('connecting')
  })

  it('has nothing to rejoin when there is no call, or one that is only ringing here', async () => {
    rig.controller.onReconnected()
    await settle()

    expect(rig.api.rejoined).toEqual([])
  })
})
