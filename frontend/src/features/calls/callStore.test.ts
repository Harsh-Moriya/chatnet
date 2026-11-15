import { beforeEach, describe, expect, it } from 'vitest'
import { inCall, useCallStore } from './callStore'

const store = () => useCallStore.getState()
const people = { conversationId: 'c1', peerId: 'p1', peerName: 'Ben', media: 'Video' as const }

beforeEach(() => store().clear())

describe('a call made here', () => {
  it('goes from starting to calling, answered, connected and ended, and then clears', () => {
    store().begin(people)
    expect(store()).toMatchObject({ phase: 'starting', outgoing: true, sendingVideo: true, callId: null })

    store().calling('call-1', '2030-01-01T00:00:30Z')
    expect(store()).toMatchObject({ phase: 'outgoing', callId: 'call-1', ringsUntil: '2030-01-01T00:00:30Z' })

    store().answered(1000)
    expect(store()).toMatchObject({ phase: 'connecting', answeredAt: 1000, ringsUntil: null })

    store().connected()
    expect(store().phase).toBe('active')

    store().finish('Completed', null)
    expect(store()).toMatchObject({ phase: 'ended', endedReason: 'Completed' })

    store().clear()
    expect(store()).toMatchObject({ phase: 'idle', callId: null, peerName: undefined, endedReason: null, notice: null })
  })

  it('does not start while another call is in progress', () => {
    store().begin(people)
    store().begin({ ...people, peerName: 'Somebody else' })

    expect(store().peerName).toBe('Ben')
  })
})

describe('a call coming in', () => {
  it('rings and, once answered, connects', () => {
    store().ring({ ...people, callId: 'call-2', ringsUntil: '2030-01-01T00:00:30Z' })
    expect(store()).toMatchObject({ phase: 'incoming', outgoing: false, callId: 'call-2' })

    store().answered(2000, false)
    expect(store()).toMatchObject({ phase: 'connecting', sendingVideo: false })
  })

  it('does not ring over a call that is in progress', () => {
    store().begin(people)
    store().ring({ ...people, peerName: 'Intruder', callId: 'call-3', ringsUntil: 'x' })

    expect(store()).toMatchObject({ phase: 'starting', peerName: 'Ben' })
  })

  it('is a video call that is answered as video unless the answer says otherwise', () => {
    store().ring({ ...people, callId: 'call-2', ringsUntil: 'x' })
    store().answered(1)

    expect(store().sendingVideo).toBe(true)
  })
})

describe('moves that do not fit where the call is', () => {
  it('are ignored', () => {
    store().connected()
    store().answered(1)
    store().calling('x', 'y')
    store().finish('Failed', 'nothing was going on')
    expect(store().phase).toBe('idle')

    store().begin(people)
    store().connected()
    expect(store().phase).toBe('starting')
  })

  it('cannot end a call that has ended, so the first account of how it ended stands', () => {
    store().begin(people)
    store().finish('Declined', 'first')
    store().finish('Failed', 'second')

    expect(store()).toMatchObject({ endedReason: 'Declined', notice: 'first' })
  })
})

describe('what the person controls and is told', () => {
  it('keeps mute, camera, the notice and the streams', () => {
    store().begin(people)
    const stream = {} as MediaStream

    store().setMuted(true)
    store().setCameraOff(true)
    store().setNotice('Allow the microphone')
    store().setStreams({ local: stream })

    expect(store()).toMatchObject({ muted: true, cameraOff: true, notice: 'Allow the microphone', localStream: stream, remoteStream: null })

    store().setStreams({ remote: stream })
    expect(store().localStream).toBe(stream)
    expect(store().remoteStream).toBe(stream)
  })

  it('counts a call as in progress from the moment it starts until it has ended', () => {
    expect(inCall('idle')).toBe(false)
    expect(inCall('ended')).toBe(false)
    for (const phase of ['starting', 'outgoing', 'incoming', 'connecting', 'active'] as const) expect(inCall(phase)).toBe(true)
  })
})
