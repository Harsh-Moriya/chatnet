import { describe, expect, it } from 'vitest'
import { isTokenExpired } from './jwt'

// A token is three base64url parts; only the middle one (the payload) is read here, to see when it
// expires. The signature is never checked on the client: the server is the one that decides.
function token(payload: object): string {
  const encode = (value: object) => btoa(JSON.stringify(value)).replaceAll('+', '-').replaceAll('/', '_').replaceAll('=', '')
  return `${encode({ alg: 'HS256', typ: 'JWT' })}.${encode(payload)}.signature`
}

const NOW = Date.UTC(2026, 9, 11, 12, 0, 0)
const seconds = (date: number) => Math.floor(date / 1000)

describe('isTokenExpired', () => {
  it('is false while the token has time left', () => {
    expect(isTokenExpired(token({ exp: seconds(NOW) + 60 }), NOW)).toBe(false)
  })

  it('is true once the expiry has passed', () => {
    expect(isTokenExpired(token({ exp: seconds(NOW) - 1 }), NOW)).toBe(true)
  })

  it('is true at the exact second of expiry', () => {
    expect(isTokenExpired(token({ exp: seconds(NOW) }), NOW)).toBe(true)
  })

  it('reads a payload that needs base64url characters and padding handled', () => {
    const payload = { exp: seconds(NOW) + 3600, display_name: 'Zoë ??>>' + '~'.repeat(5) }

    expect(isTokenExpired(token(payload), NOW)).toBe(false)
  })

  it('treats no token as expired', () => {
    expect(isTokenExpired(null, NOW)).toBe(true)
    expect(isTokenExpired('', NOW)).toBe(true)
  })

  it('treats a token it cannot read as expired, so a broken session is ended rather than kept', () => {
    expect(isTokenExpired('not-a-token', NOW)).toBe(true)
    expect(isTokenExpired('a.b.c', NOW)).toBe(true)
    expect(isTokenExpired(token({ no_exp: true }), NOW)).toBe(true)
    expect(isTokenExpired(token({ exp: 'soon' }), NOW)).toBe(true)
  })
})
