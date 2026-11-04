import { describe, expect, it } from 'vitest'
import { decodeKey, toRegistration, usesKey } from './pushKeys'

// The example public key of RFC 8291, which is 65 bytes starting with 0x04.
const KEY = 'BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4'

describe('decoding a key', () => {
  it('gives the 65 bytes of an uncompressed point, the first of them 0x04', () => {
    const bytes = decodeKey(KEY)

    expect(bytes).toHaveLength(65)
    expect(bytes[0]).toBe(0x04)
  })

  it('reads the url-safe alphabet and needs no padding', () => {
    // 0xfb 0xff is "-_8" in base64url and "+/8=" in base64.
    expect([...decodeKey('-_8')]).toEqual([0xfb, 0xff])
    expect([...decodeKey('-_8=')]).toEqual([0xfb, 0xff])
  })

  it('gives nothing for nothing', () => {
    expect(decodeKey('')).toHaveLength(0)
  })
})

describe('whether a subscription was made with the key of the server', () => {
  it('is yes for the same key', () => {
    expect(usesKey(decodeKey(KEY).buffer, KEY)).toBe(true)
  })

  it('is no for another key of the same length', () => {
    const other = decodeKey(KEY)
    other[64] ^= 1

    expect(usesKey(other.buffer, KEY)).toBe(false)
  })

  it('is no for a key of another length, and for no key', () => {
    expect(usesKey(decodeKey(KEY).slice(0, 64).buffer, KEY)).toBe(false)
    expect(usesKey(null, KEY)).toBe(false)
  })
})

describe('what is sent to the server', () => {
  it('keeps the endpoint and the two keys in the shape the server reads', () => {
    expect(toRegistration({ endpoint: 'https://push.example/abc', keys: { p256dh: 'k', auth: 'a' } })).toEqual({
      endpoint: 'https://push.example/abc',
      keys: { p256dh: 'k', auth: 'a' },
    })
  })

  it('drops what the server has no use for, such as when the subscription expires', () => {
    const registration = toRegistration({
      endpoint: 'https://push.example/abc',
      expirationTime: 1234,
      keys: { p256dh: 'k', auth: 'a' },
    })

    expect(registration).toEqual({ endpoint: 'https://push.example/abc', keys: { p256dh: 'k', auth: 'a' } })
  })

  it.each([
    ['no endpoint', { keys: { p256dh: 'k', auth: 'a' } }],
    ['an empty endpoint', { endpoint: '', keys: { p256dh: 'k', auth: 'a' } }],
    ['no keys', { endpoint: 'https://push.example/abc' }],
    ['no public key', { endpoint: 'https://push.example/abc', keys: { auth: 'a' } }],
    ['no secret', { endpoint: 'https://push.example/abc', keys: { p256dh: 'k' } }],
  ])('is nothing for a subscription with %s', (_, json) => {
    expect(toRegistration(json)).toBeNull()
  })
})
