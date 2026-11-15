import { describe, expect, it } from 'vitest'
import { callDuration, callLabel, callPreview, callSeconds, countsAsUnread } from './calls'
import type { CallEndReason, CallInfo } from './types'

function call(overrides: Partial<CallInfo> = {}): CallInfo {
  return {
    id: 'call-1',
    media: 'Audio',
    startedAt: '2026-11-15T10:00:00Z',
    answeredAt: null,
    endedAt: '2026-11-15T10:00:30Z',
    endReason: 'Missed',
    isMissed: true,
    ...overrides,
  }
}

const answered = (overrides: Partial<CallInfo> = {}) =>
  call({ answeredAt: '2026-11-15T10:00:05Z', endedAt: '2026-11-15T10:04:17Z', endReason: 'Completed', isMissed: false, ...overrides })

describe('what counts as unread', () => {
  it('is any text', () => {
    expect(countsAsUnread({})).toBe(true)
    expect(countsAsUnread({ kind: 'Text', call: null })).toBe(true)
  })

  it('is a call only when it was missed, as the server says', () => {
    expect(countsAsUnread({ kind: 'Call', call: call() })).toBe(true)
    expect(countsAsUnread({ kind: 'Call', call: answered() })).toBe(false)
    expect(countsAsUnread({ kind: 'Call', call: call({ endReason: 'Declined', isMissed: false }) })).toBe(false)
  })
})

describe('how long a call ran', () => {
  it('is the time from the answer to the end', () => {
    expect(callSeconds(answered())).toBe(252)
    expect(callDuration(answered())).toBe('4:12')
  })

  it('is an hour and more in hours', () => {
    expect(callDuration(answered({ endedAt: '2026-11-15T11:10:09Z' }))).toBe('1:10:04')
  })

  it('is nothing for a call nobody answered', () => {
    expect(callSeconds(call())).toBeNull()
    expect(callDuration(call())).toBeNull()
  })
})

describe('the words for a call', () => {
  it('name the kind of call that was answered, whoever made it', () => {
    expect(callLabel(answered(), true)).toBe('Audio call')
    expect(callLabel(answered({ media: 'Video' }), false)).toBe('Video call')
  })

  it('say a connection that broke after the answer dropped the call', () => {
    expect(callLabel(answered({ endReason: 'Failed' }), true)).toBe('Dropped audio call')
  })

  it.each<[CallEndReason, string]>([
    ['Missed', 'No answer'],
    ['Declined', 'Call declined'],
    ['Busy', 'Line busy'],
    ['Cancelled', 'Cancelled audio call'],
    ['Failed', 'Call failed'],
  ])('tell the caller of a call that ended %s', (endReason, words) => {
    expect(callLabel(call({ endReason }), true)).toBe(words)
  })

  it.each<CallEndReason>(['Missed', 'Cancelled', 'Busy', 'Failed'])('tell the callee of a call that ended %s that it was missed', (endReason) => {
    expect(callLabel(call({ endReason, media: 'Video' }), false)).toBe('Missed video call')
  })

  it('tell the callee who turned it down that they did', () => {
    expect(callLabel(call({ endReason: 'Declined', isMissed: false }), false)).toBe('Declined audio call')
  })

  it('add the length to the line of the sidebar when there is one', () => {
    expect(callPreview(answered(), true)).toBe('Audio call, 4:12')
    expect(callPreview(call(), false)).toBe('Missed audio call')
  })
})
