import { describe, expect, it } from 'vitest'
import { canRetryAfterRenewal } from './unauthorizedRetry'

// A 401 is worth one more try after renewing the session only when the request carried a token and has not
// been sent again. Getting this wrong either signs people out for nothing or loops against a server that
// keeps saying no.
describe('canRetryAfterRenewal', () => {
  const withToken = { headers: { Authorization: 'Bearer t1' } }

  it('retries a request that carried a token and was refused', () => {
    expect(canRetryAfterRenewal(401, withToken)).toBe(true)
  })

  it('does not retry a request that carried no token, such as a failed sign-in', () => {
    expect(canRetryAfterRenewal(401, { headers: {} })).toBe(false)
    expect(canRetryAfterRenewal(401, {})).toBe(false)
    expect(canRetryAfterRenewal(401, { headers: { Authorization: '' } })).toBe(false)
  })

  it('does not retry a request twice', () => {
    expect(canRetryAfterRenewal(401, { ...withToken, retriedAfterRenewal: true })).toBe(false)
  })

  it('does not retry any other answer', () => {
    for (const status of [undefined, 200, 400, 403, 404, 409, 429, 500]) {
      expect(canRetryAfterRenewal(status, withToken)).toBe(false)
    }
  })

  it('does not retry when there is no request to send again', () => {
    expect(canRetryAfterRenewal(401, undefined)).toBe(false)
  })
})
