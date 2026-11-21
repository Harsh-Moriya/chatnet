import { describe, expect, it } from 'vitest'
import { callErrorCode } from './callErrors'
import { callStatus, endedNotice, startRefusal } from './callTexts'

describe('the line under the other person\'s name', () => {
  it.each([
    ['starting', 'Starting the call'],
    ['outgoing', 'Calling'],
    ['connecting', 'Connecting'],
    ['ended', 'Call ended'],
  ] as const)('says what is happening while %s', (phase, words) => {
    expect(callStatus({ phase, reconnecting: false, elapsedSeconds: 0 })).toBe(words)
  })

  it('shows how long an active call has lasted', () => {
    expect(callStatus({ phase: 'active', reconnecting: false, elapsedSeconds: 75 })).toBe('1:15')
    expect(callStatus({ phase: 'active', reconnecting: false, elapsedSeconds: 3725 })).toBe('1:02:05')
  })

  it('says a call whose media dropped is reconnecting, and a call that ended is not', () => {
    expect(callStatus({ phase: 'active', reconnecting: true, elapsedSeconds: 75 })).toBe('Reconnecting')
    expect(callStatus({ phase: 'ended', reconnecting: true, elapsedSeconds: 75 })).toBe('Call ended')
  })
})

describe('what a call that ended says', () => {
  const base = { outgoing: true, answered: false, peerName: 'Ben' }

  it.each([
    ['Declined', 'Ben declined the call.'],
    ['Missed', 'Ben did not answer.'],
    ['Busy', 'Ben is in another call.'],
    ['Failed', 'The call could not be connected.'],
  ] as const)('%s', (reason, words) => {
    expect(endedNotice({ ...base, reason })).toBe(words)
  })

  it('says a call that was answered and then failed was dropped', () => {
    expect(endedNotice({ ...base, reason: 'Failed', answered: true })).toBe('The call was dropped.')
  })

  it.each(['Completed', 'Cancelled', null] as const)('says nothing more than "Call ended" for %s', (reason) => {
    expect(endedNotice({ ...base, reason })).toBeNull()
  })
})

describe('why a call could not be started', () => {
  it.each([
    ['already-in-a-call', 'You are already in a call.'],
    ['callee-busy', 'Ben is in another call.'],
    ['peer-unavailable', 'Ben cannot be called.'],
    ['rate-limited', 'You are calling too often. Try again in a minute.'],
    ['calls-unavailable', 'Calls are not available right now. Try again in a moment.'],
    ['not-a-member', 'The call could not be started.'],
    [null, 'The call could not be started.'],
  ])('%s', (code, words) => {
    expect(startRefusal(code, 'Ben')).toBe(words)
  })
})

describe('the code of a refusal', () => {
  it('is found inside the words the client library puts around it', () => {
    expect(callErrorCode(new Error("An unexpected error occurred invoking 'StartCall' on the server. HubException: callee-busy"))).toBe('callee-busy')
  })

  it('is read from a plain message too', () => {
    expect(callErrorCode('call-not-ringing')).toBe('call-not-ringing')
  })

  it('is null for anything else', () => {
    expect(callErrorCode(new Error('Network down'))).toBeNull()
    expect(callErrorCode(undefined)).toBeNull()
    expect(callErrorCode(42)).toBeNull()
  })
})
