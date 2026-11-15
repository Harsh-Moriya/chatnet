import { describe, expect, it } from 'vitest'
import { toBase64Url } from '../../lib/crypto/bytes'
import { MAX_CANDIDATE_LENGTH, MAX_SDP_LENGTH, decodeCandidate, decodeSdpSignal, encodeCandidate, encodeSdpSignal } from './signals'

const signature = new Uint8Array(64).fill(7)

describe('an offer or an answer', () => {
  it('carries the description and its signature, and reads back', () => {
    const decoded = decodeSdpSignal(encodeSdpSignal('v=0\r\n', signature))

    expect(decoded?.sdp).toBe('v=0\r\n')
    expect([...(decoded?.signature ?? [])]).toEqual([...signature])
  })

  it('is JSON with the signature in base64url', () => {
    expect(JSON.parse(encodeSdpSignal('x', signature))).toEqual({ sdp: 'x', signature: toBase64Url(signature) })
  })

  it.each([
    ['not JSON', 'not json'],
    ['an array', '[]'],
    ['null', 'null'],
    ['a string', '"sdp"'],
    ['no sdp', JSON.stringify({ signature: toBase64Url(signature) })],
    ['an empty sdp', JSON.stringify({ sdp: '', signature: toBase64Url(signature) })],
    ['an sdp that is not text', JSON.stringify({ sdp: 5, signature: toBase64Url(signature) })],
    ['a description that is too long', JSON.stringify({ sdp: 'x'.repeat(MAX_SDP_LENGTH + 1), signature: toBase64Url(signature) })],
    ['no signature', JSON.stringify({ sdp: 'x' })],
    ['a signature that is not text', JSON.stringify({ sdp: 'x', signature: [1, 2] })],
    ['a signature that is not base64url', JSON.stringify({ sdp: 'x', signature: '!!!' })],
    ['a signature of the wrong length', JSON.stringify({ sdp: 'x', signature: toBase64Url(new Uint8Array(63)) })],
  ])('is refused when it is %s', (_name, payload) => {
    expect(decodeSdpSignal(payload)).toBeNull()
  })

  it('may be as long as the limit', () => {
    expect(decodeSdpSignal(encodeSdpSignal('x'.repeat(MAX_SDP_LENGTH), signature))).not.toBeNull()
  })
})

describe('a candidate', () => {
  const candidate = { candidate: 'candidate:1 1 udp 2113937151 10.0.0.1 5000 typ host', sdpMid: '0', sdpMLineIndex: 0, usernameFragment: 'abcd' }

  it('reads back as it was sent', () => {
    expect(decodeCandidate(encodeCandidate(candidate))).toEqual(candidate)
  })

  it('may leave out what a browser leaves out', () => {
    expect(decodeCandidate(JSON.stringify({ candidate: 'candidate:1' }))).toEqual({
      candidate: 'candidate:1',
      sdpMid: null,
      sdpMLineIndex: null,
      usernameFragment: null,
    })
  })

  it.each([
    ['not JSON', '{'],
    ['an array', '[]'],
    ['no candidate', JSON.stringify({ sdpMid: '0' })],
    ['a candidate that is not text', JSON.stringify({ candidate: 1 })],
    ['a candidate that is too long', JSON.stringify({ candidate: 'x'.repeat(MAX_CANDIDATE_LENGTH + 1) })],
    ['a mid that is not text', JSON.stringify({ candidate: 'c', sdpMid: 0 })],
    ['a line index that is not a number', JSON.stringify({ candidate: 'c', sdpMLineIndex: '0' })],
    ['a line index that is negative', JSON.stringify({ candidate: 'c', sdpMLineIndex: -1 })],
    ['a line index that is not whole', JSON.stringify({ candidate: 'c', sdpMLineIndex: 0.5 })],
    ['a username fragment that is not text', JSON.stringify({ candidate: 'c', usernameFragment: 7 })],
  ])('is refused when it has %s', (_name, payload) => {
    expect(decodeCandidate(payload)).toBeNull()
  })
})
