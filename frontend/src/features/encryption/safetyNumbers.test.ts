import { describe, expect, it } from 'vitest'
import {
  CryptoError,
  type AccountSecrets,
  createAccountSecrets,
  currentAccountKey,
  equal,
  signBundle,
  toBase64Url,
} from '../../lib/crypto'
import { MemoryPinStore } from './fakeEpochServer'
import { IdentityChangedError } from './pins'
import { MissingKeysError } from './epochs'
import { acceptChangedIdentity, loadSafetyView, setVerified, type SafetyDeps } from './safetyNumbers'
import type { UserBundleWire } from './types'

// The number two people compare, whether a person is marked as verified, and what happens when their key changes. The real
// cryptography, with the server's answer to "what are this person's keys" made by the test.

const ANA = '3f2a9c1e-7b4d-4e8a-9c55-0a1b2c3d4e5f'
const BEN = '9d8c7b6a-5e4f-4a3b-8c2d-1e0f9a8b7c6d'

async function person(userId: string) {
  const secrets = await createAccountSecrets()
  return { userId, secrets }
}

async function wireOf(userId: string, secrets: AccountSecrets): Promise<UserBundleWire> {
  const bundle = await signBundle(userId, secrets.identity, currentAccountKey(secrets))
  return { userId, generation: currentAccountKey(secrets).generation, bundle: toBase64Url(bundle) }
}

// What the server holds, by person, and a way for a test to change it.
function server(...entries: UserBundleWire[]) {
  const held = new Map(entries.map((entry) => [entry.userId, entry]))
  return {
    held,
    api: { lookupKeys: async (userIds: string[]) => userIds.flatMap((id) => held.get(id) ?? []) },
  }
}

function depsFor(owner: { userId: string; secrets: AccountSecrets }, api: SafetyDeps['api'], pins = new MemoryPinStore()): SafetyDeps {
  return { api, pins, self: { userId: owner.userId, identityPublic: owner.secrets.identity.publicKey } }
}

describe('the safety number', () => {
  it('is the same on both screens, so two people who see the same one know nobody is between them', async () => {
    const ana = await person(ANA)
    const ben = await person(BEN)
    const { api } = server(await wireOf(ANA, ana.secrets), await wireOf(BEN, ben.secrets))

    const seenByAna = await loadSafetyView(depsFor(ana, api), BEN)
    const seenByBen = await loadSafetyView(depsFor(ben, api), ANA)

    expect(seenByAna.number).toBe(seenByBen.number)
    expect(seenByAna.number).toMatch(/^(\d{5} ){11}\d{5}$/)
  })

  it('differs when someone has put a key of their own between the two', async () => {
    const ana = await person(ANA)
    const ben = await person(BEN)
    const mallory = await person(BEN)
    // Ana is told Mallory's key is Ben's, and Ben is told Ana's own key, which is what he really has.
    const seenByAna = await loadSafetyView(depsFor(ana, server(await wireOf(BEN, mallory.secrets)).api), BEN)
    const seenByBen = await loadSafetyView(depsFor(ben, server(await wireOf(ANA, ana.secrets)).api), ANA)

    expect(seenByAna.number).not.toBe(seenByBen.number)
  })

  it('is made from the key this browser remembers for the person, not from whatever is offered next', async () => {
    const ana = await person(ANA)
    const ben = await person(BEN)
    const pins = new MemoryPinStore()
    const { api, held } = server(await wireOf(BEN, ben.secrets))
    const first = await loadSafetyView(depsFor(ana, api, pins), BEN)

    held.set(BEN, await wireOf(BEN, (await person(BEN)).secrets))
    const pinned = (await pins.load(ANA, BEN))!.identityPublic
    const later = await loadSafetyView(depsFor(ana, api, pins), BEN)

    expect(equal(pinned, ben.secrets.identity.publicKey)).toBe(true)
    expect(later.number).not.toBe(first.number)
    expect(later.changedTo).not.toBeNull()
  })

  it('remembers the person the first time they are seen, and shows no change', async () => {
    const ana = await person(ANA)
    const ben = await person(BEN)
    const pins = new MemoryPinStore()

    const view = await loadSafetyView(depsFor(ana, server(await wireOf(BEN, ben.secrets)).api, pins), BEN)

    expect(view.changedTo).toBeNull()
    expect(view.verifiedAt).toBeNull()
    expect(equal((await pins.load(ANA, BEN))!.identityPublic, ben.secrets.identity.publicKey)).toBe(true)
  })

  it('says so when the person has not set up encryption', async () => {
    const ana = await person(ANA)

    await expect(loadSafetyView(depsFor(ana, server().api), BEN)).rejects.toBeInstanceOf(MissingKeysError)
  })

  it('refuses a bundle that was made for another person', async () => {
    const ana = await person(ANA)
    const ben = await person(BEN)
    const forOther = await wireOf('00000000-0000-4000-8000-000000000001', ben.secrets)

    await expect(loadSafetyView(depsFor(ana, server({ ...forOther, userId: BEN }).api), BEN)).rejects.toBeInstanceOf(CryptoError)
  })
})

describe('verifying', () => {
  async function arranged() {
    const ana = await person(ANA)
    const ben = await person(BEN)
    const pins = new MemoryPinStore()
    const s = server(await wireOf(BEN, ben.secrets))
    const deps = depsFor(ana, s.api, pins)
    await loadSafetyView(deps, BEN)
    return { ana, ben, pins, s, deps }
  }

  it('marks the person, and the mark is there the next time the number is shown', async () => {
    const { deps } = await arranged()

    await setVerified(deps, BEN, true)

    expect((await loadSafetyView(deps, BEN)).verifiedAt).not.toBeNull()
  })

  it('takes the mark away again', async () => {
    const { deps } = await arranged()
    await setVerified(deps, BEN, true)

    await setVerified(deps, BEN, false)

    expect((await loadSafetyView(deps, BEN)).verifiedAt).toBeNull()
  })

  it('keeps the key it marks: nothing else about the remembered person changes', async () => {
    const { ben, pins, deps } = await arranged()
    const before = (await pins.load(ANA, BEN))!

    await setVerified(deps, BEN, true)

    const after = (await pins.load(ANA, BEN))!
    expect(equal(after.identityPublic, ben.secrets.identity.publicKey)).toBe(true)
    expect(after.firstSeenAt).toBe(before.firstSeenAt)
  })

  it('has nothing to mark for a person who was never seen', async () => {
    const ana = await person(ANA)

    await expect(setVerified(depsFor(ana, server().api), BEN, true)).rejects.toThrow()
  })

  it('does not mark a person for one of two people: it is the pair of this owner and this contact', async () => {
    const { deps, pins } = await arranged()

    await setVerified(deps, BEN, true)

    expect(await pins.load(BEN, ANA)).toBeNull()
  })
})

describe('a key that changed', () => {
  async function verifiedAndChanged() {
    const ana = await person(ANA)
    const ben = await person(BEN)
    const pins = new MemoryPinStore()
    const s = server(await wireOf(BEN, ben.secrets))
    const deps = depsFor(ana, s.api, pins)
    await loadSafetyView(deps, BEN)
    await setVerified(deps, BEN, true)
    const reset = await person(BEN)
    s.held.set(BEN, await wireOf(BEN, reset.secrets))
    return { ana, ben, reset, pins, deps }
  }

  it('is reported with the key now offered, and the number shown is for that key and not marked verified', async () => {
    const { reset, deps } = await verifiedAndChanged()

    const view = await loadSafetyView(deps, BEN)

    expect(equal(view.changedTo!, reset.secrets.identity.publicKey)).toBe(true)
    expect(view.verifiedAt).toBeNull()
  })

  it('is not remembered until it is accepted', async () => {
    const { ben, pins, deps } = await verifiedAndChanged()

    await loadSafetyView(deps, BEN)

    expect(equal((await pins.load(ANA, BEN))!.identityPublic, ben.secrets.identity.publicKey)).toBe(true)
  })

  it('is remembered when accepted, and then shows no change and carries no verified mark', async () => {
    const { reset, pins, deps } = await verifiedAndChanged()
    const view = await loadSafetyView(deps, BEN)

    await acceptChangedIdentity(deps, BEN, view.changedTo!)

    const after = await loadSafetyView(deps, BEN)
    expect(after.changedTo).toBeNull()
    expect(after.verifiedAt).toBeNull()
    expect(after.number).toBe(view.number)
    expect(equal((await pins.load(ANA, BEN))!.identityPublic, reset.secrets.identity.publicKey)).toBe(true)
  })

  it('is the same IdentityChangedError that sealing a key to the person raises', async () => {
    const { ben, reset, pins } = await verifiedAndChanged()
    const { pinIdentity } = await import('./pins')

    await expect(pinIdentity(pins, ANA, BEN, reset.secrets.identity.publicKey)).rejects.toBeInstanceOf(IdentityChangedError)
    expect(equal((await pins.load(ANA, BEN))!.identityPublic, ben.secrets.identity.publicKey)).toBe(true)
  })
})
