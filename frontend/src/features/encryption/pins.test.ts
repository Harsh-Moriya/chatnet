import { describe, expect, it } from 'vitest'
import { MemoryPinStore } from './fakeEpochServer'
import { IdentityChangedError, pinIdentity, trustNewIdentity } from './pins'

const OWNER = '3f2a9c1e-7b4d-4e8a-9c55-0a1b2c3d4e5f'
const ANA = '9d8c7b6a-5e4f-4a3b-8c2d-1e0f9a8b7c6d'
const BEN = '0a1b2c3d-4e5f-4a6b-8c7d-9e8f7a6b5c4d'

const key = (...bytes: number[]) => Uint8Array.from(bytes)

describe('remembering who is who', () => {
  it('keeps the identity key of a person the first time they are seen', async () => {
    const pins = new MemoryPinStore()

    const first = await pinIdentity(pins, OWNER, ANA, key(1, 2, 3))

    expect(first).toBe('new')
    const kept = await pins.load(OWNER, ANA)
    expect(Array.from(kept!.identityPublic)).toEqual([1, 2, 3])
    expect(Number.isNaN(Date.parse(kept!.firstSeenAt))).toBe(false)
  })

  it('accepts the same key again and keeps the time it was first seen', async () => {
    const pins = new MemoryPinStore()
    await pinIdentity(pins, OWNER, ANA, key(1, 2, 3))
    const firstSeen = (await pins.load(OWNER, ANA))!.firstSeenAt

    const again = await pinIdentity(pins, OWNER, ANA, key(1, 2, 3))

    expect(again).toBe('same')
    expect((await pins.load(OWNER, ANA))!.firstSeenAt).toBe(firstSeen)
  })

  it('refuses a different key, names the person and offers the key it saw, and keeps the old one', async () => {
    const pins = new MemoryPinStore()
    await pinIdentity(pins, OWNER, ANA, key(1, 2, 3))

    const failure = await pinIdentity(pins, OWNER, ANA, key(9, 9, 9)).catch((error: unknown) => error)

    expect(failure).toBeInstanceOf(IdentityChangedError)
    expect((failure as IdentityChangedError).userId).toBe(ANA)
    expect(Array.from((failure as IdentityChangedError).seen)).toEqual([9, 9, 9])
    expect(Array.from((await pins.load(OWNER, ANA))!.identityPublic)).toEqual([1, 2, 3])
  })

  it('refuses a key that differs in one byte', async () => {
    const pins = new MemoryPinStore()
    await pinIdentity(pins, OWNER, ANA, key(1, 2, 3))

    await expect(pinIdentity(pins, OWNER, ANA, key(1, 2, 4))).rejects.toBeInstanceOf(IdentityChangedError)
    await expect(pinIdentity(pins, OWNER, ANA, key(1, 2))).rejects.toBeInstanceOf(IdentityChangedError)
  })

  it('takes the new key once the person looking has accepted it', async () => {
    const pins = new MemoryPinStore()
    await pinIdentity(pins, OWNER, ANA, key(1, 2, 3))

    await trustNewIdentity(pins, OWNER, ANA, key(9, 9, 9))

    expect(await pinIdentity(pins, OWNER, ANA, key(9, 9, 9))).toBe('same')
    await expect(pinIdentity(pins, OWNER, ANA, key(1, 2, 3))).rejects.toBeInstanceOf(IdentityChangedError)
  })

  it('keeps each person apart, and each person who is looking apart', async () => {
    const pins = new MemoryPinStore()
    await pinIdentity(pins, OWNER, ANA, key(1))

    expect(await pinIdentity(pins, OWNER, BEN, key(2))).toBe('new')
    expect(await pinIdentity(pins, BEN, ANA, key(3))).toBe('new')
    expect(await pinIdentity(pins, OWNER, ANA, key(1))).toBe('same')
  })
})
