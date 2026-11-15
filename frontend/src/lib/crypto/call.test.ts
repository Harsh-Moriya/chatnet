import { describe, expect, it } from 'vitest'
import { CryptoError } from './bytes'
import { MAX_FINGERPRINTS, callSignatureInput, fingerprintsOf, signCall, verifyCall, type CallContext } from './call'
import { newEd25519 } from './primitives'

const CALL = 'ca11ca11-0000-4000-8000-000000000001'
const CONVERSATION = 'c0ffee00-1234-4abc-8def-0123456789ab'
const ALICE = '3f2a9c1e-7b4d-4e8a-9c55-0a1b2c3d4e5f'
const BOB = '9d8c7b6a-5f4e-4d3c-8b2a-1f0e9d8c7b6a'

const FP = 'sha-256 AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99'
const sdpWith = (...fingerprints: string[]) =>
  ['v=0', 'o=- 1 2 IN IP4 127.0.0.1', 's=-', ...fingerprints.map((f) => `a=fingerprint:${f}`), 'a=setup:actpass', ''].join('\r\n')

const context = (overrides: Partial<CallContext> = {}): CallContext => ({
  callId: CALL,
  conversationId: CONVERSATION,
  signerId: ALICE,
  peerId: BOB,
  ...overrides,
})

describe('the fingerprints of a session description', () => {
  it('are normalised, without repeats, and sorted, however the browser wrote them', () => {
    const sdp = sdpWith('SHA-256 aa:bb:cc', 'sha-256 AA:BB:CC', 'sha-1 01:02')

    expect(fingerprintsOf(sdp)).toEqual(['sha-1 01:02', 'sha-256 AA:BB:CC'])
  })

  it('are read from lines ended by either kind of line end', () => {
    expect(fingerprintsOf(`v=0\na=fingerprint:sha-256 AA:BB\na=setup:active\n`)).toEqual(['sha-256 AA:BB'])
  })

  it('are required: a description with none is refused', () => {
    expect(() => fingerprintsOf('v=0\r\ns=-\r\n')).toThrow(CryptoError)
  })

  it.each(['a=fingerprint:sha-256', 'a=fingerprint:sha-256 ZZ:ZZ', 'a=fingerprint: AA:BB', 'a=fingerprint:sha-256 AA:B'])(
    'must be well formed: %s',
    (line) => {
      expect(() => fingerprintsOf(`v=0\r\n${line}\r\n`)).toThrow(CryptoError)
    },
  )

  it('are limited in number', () => {
    const many = Array.from({ length: MAX_FINGERPRINTS + 1 }, (_, i) => `sha-256 ${i.toString(16).padStart(2, '0').toUpperCase()}:00`)

    expect(() => fingerprintsOf(sdpWith(...many))).toThrow(CryptoError)
  })
})

describe('signing a call', () => {
  it('is checked by the signer’s public key over the same description', async () => {
    const alice = await newEd25519()
    const sdp = sdpWith(FP)

    const signature = await signCall(alice, context(), 'offer', sdp)

    expect(signature).toHaveLength(64)
    expect(await verifyCall(alice.publicKey, context(), 'offer', sdp, signature)).toBe(true)
  })

  it('does not depend on how the browser wrote the lines', async () => {
    const alice = await newEd25519()
    const signature = await signCall(alice, context(), 'offer', sdpWith(FP.toUpperCase().replace('SHA-256', 'sha-256')))

    expect(await verifyCall(alice.publicKey, context(), 'offer', `${sdpWith(FP)}a=extra:1\r\n`, signature)).toBe(true)
  })

  describe('is not accepted when anything it covers was changed on the way', () => {
    const cases: [string, (sdp: string) => { sdp: string; context?: CallContext; role?: 'offer' | 'answer' }][] = [
      ['a fingerprint was changed', (sdp) => ({ sdp: sdp.replace('AA:BB', 'AA:BC') })],
      ['a fingerprint was added', (sdp) => ({ sdp: sdp.replace('a=setup', 'a=fingerprint:sha-256 DE:AD\r\na=setup') })],
      ['the fingerprint was removed and another put in its place', () => ({ sdp: sdpWith('sha-256 DE:AD:BE:EF') })],
      ['it is for another call', (sdp) => ({ sdp, context: context({ callId: 'ca11ca11-0000-4000-8000-000000000002' }) })],
      ['it is for another conversation', (sdp) => ({ sdp, context: context({ conversationId: 'c0ffee00-1234-4abc-8def-0123456789ac' }) })],
      ['it was made by another person', (sdp) => ({ sdp, context: context({ signerId: BOB, peerId: ALICE }) })],
      ['it is for another recipient', (sdp) => ({ sdp, context: context({ peerId: '00000000-0000-4000-8000-000000000009' }) })],
      ['an offer is passed off as an answer', (sdp) => ({ sdp, role: 'answer' })],
    ]

    it.each(cases)('%s', async (_name, change) => {
      const alice = await newEd25519()
      const sdp = sdpWith(FP)
      const signature = await signCall(alice, context(), 'offer', sdp)
      const changed = change(sdp)

      expect(await verifyCall(alice.publicKey, changed.context ?? context(), changed.role ?? 'offer', changed.sdp, signature)).toBe(false)
    })
  })

  it('is not accepted under another key', async () => {
    const [alice, mallory] = await Promise.all([newEd25519(), newEd25519()])
    const signature = await signCall(alice, context(), 'offer', sdpWith(FP))

    expect(await verifyCall(mallory.publicKey, context(), 'offer', sdpWith(FP), signature)).toBe(false)
  })

  it('is not accepted when a bit of it was flipped or its length is not a signature’s', async () => {
    const alice = await newEd25519()
    const signature = await signCall(alice, context(), 'offer', sdpWith(FP))
    const flipped = new Uint8Array(signature)
    flipped[10] ^= 1

    expect(await verifyCall(alice.publicKey, context(), 'offer', sdpWith(FP), flipped)).toBe(false)
    expect(await verifyCall(alice.publicKey, context(), 'offer', sdpWith(FP), signature.subarray(0, 63))).toBe(false)
    expect(await verifyCall(alice.publicKey, context(), 'offer', sdpWith(FP), new Uint8Array(65))).toBe(false)
  })

  it('refuses a description with no fingerprint instead of signing or accepting nothing', async () => {
    const alice = await newEd25519()

    await expect(signCall(alice, context(), 'offer', 'v=0\r\n')).rejects.toThrow(CryptoError)
    await expect(verifyCall(alice.publicKey, context(), 'offer', 'v=0\r\n', new Uint8Array(64))).rejects.toThrow(CryptoError)
  })

  it('has an input that starts with the label and covers the ids, the role and each fingerprint with its length', () => {
    const input = callSignatureInput(context(), 'answer', ['sha-256 AA'])

    const label = new TextEncoder().encode('chatnet/v1/call-signature')
    expect([...input.subarray(0, label.length)]).toEqual([...label])
    expect(input[label.length]).toBe(0)
    // After the label and its zero: four ids of 16 bytes, the role, the count and the one fingerprint (10 bytes, with its length).
    expect(input).toHaveLength(label.length + 1 + 64 + 1 + 2 + 2 + 'sha-256 AA'.length)
    expect(input[label.length + 1 + 64]).toBe(1)
  })
})
