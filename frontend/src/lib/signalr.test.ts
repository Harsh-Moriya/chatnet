import { describe, expect, it } from 'vitest'
import { isUnauthorized, nextReconnectDelay, reconnectDelay } from './signalr'

// The chat should come back on its own after the network drops or the server restarts, and should
// not hammer a server that is struggling. The delays grow, then stay at a ceiling for ever: the
// client never gives up, because a laptop closed overnight should reconnect when it is opened.
describe('reconnectDelay', () => {
  it('tries again at once, then waits longer each time', () => {
    expect([0, 1, 2, 3].map(reconnectDelay)).toEqual([0, 2000, 5000, 10000])
  })

  it('settles at 30 seconds and stays there', () => {
    expect(reconnectDelay(4)).toBe(30000)
    expect(reconnectDelay(5)).toBe(30000)
    expect(reconnectDelay(10_000)).toBe(30000)
  })
})

describe('isUnauthorized', () => {
  // The message the real client library produced when this server refused a negotiate request (copied
  // from a run against the API; the library gives the error no statusCode).
  const REFUSED_NEGOTIATE =
    'Failed to complete negotiation with the server: Error: {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.2",' +
    '"title":"Unauthorized","status":401,"traceId":"00-71732d60fa5d8b1e2b56fae22a325f15-ddd084fefe138a77-01"}: Status code \'401\''

  it('is true for the error the library raises when the server refuses the token', () => {
    expect(isUnauthorized(new Error(REFUSED_NEGOTIATE))).toBe(true)
  })

  it('is false when another status is in the same message', () => {
    expect(isUnauthorized(new Error(REFUSED_NEGOTIATE.replace("'401'", "'503'")))).toBe(false)
    expect(isUnauthorized(new Error(REFUSED_NEGOTIATE.replace("'401'", "'403'")))).toBe(false)
  })

  it('is not fooled by "401" appearing elsewhere in the message, such as in a trace id', () => {
    expect(isUnauthorized(new Error("Failed to complete negotiation: {\"traceId\":\"00-401a-401b-01\"}: Status code '503'"))).toBe(false)
  })

  it('is true for an error that carries the status itself', () => {
    expect(isUnauthorized(Object.assign(new Error('Unauthorized'), { statusCode: 401 }))).toBe(true)
  })

  it('is false for any other status, and for errors that carry none', () => {
    expect(isUnauthorized(Object.assign(new Error('Forbidden'), { statusCode: 403 }))).toBe(false)
    expect(isUnauthorized(Object.assign(new Error('Unavailable'), { statusCode: 503 }))).toBe(false)
    expect(isUnauthorized(new Error('Failed to fetch'))).toBe(false)
    expect(isUnauthorized(undefined)).toBe(false)
    expect(isUnauthorized(null)).toBe(false)
    expect(isUnauthorized('401')).toBe(false)
    expect(isUnauthorized({ statusCode: '401' })).toBe(false)
  })
})

// After a connection dropped, the library asks how long to wait. A refusal is answered by renewing the
// session first, so the attempt after the wait carries a token the server accepts.
describe('nextReconnectDelay', () => {
  const unauthorized = Object.assign(new Error('Unauthorized'), { statusCode: 401 })

  it('renews the session when the last attempt was refused with a 401', () => {
    let renewals = 0

    nextReconnectDelay(1, unauthorized, () => renewals++)

    expect(renewals).toBe(1)
  })

  it('does not renew it for a drop of the network, or when nothing failed yet', () => {
    let renewals = 0

    nextReconnectDelay(0, undefined, () => renewals++)
    nextReconnectDelay(1, new Error('Failed to fetch'), () => renewals++)

    expect(renewals).toBe(0)
  })

  it('always gives the usual delay for the attempt', () => {
    expect([0, 1, 2, 3, 4].map((n) => nextReconnectDelay(n, unauthorized, () => {}))).toEqual([0, 2000, 5000, 10000, 30000])
  })
})
