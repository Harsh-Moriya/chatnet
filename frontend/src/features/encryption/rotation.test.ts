import { describe, expect, it } from 'vitest'
import { CryptoError, decodeSecrets, encodeSecrets, equal, fromBase64Url, openBackup, openBundle, toHex } from '../../lib/crypto'
import { MemoryLocalStore } from '../search/store/memoryLocalStore'
import { MemoryEpochKeyStore, MemoryPinStore } from './fakeEpochServer'
import { FakeKeysServer, MemoryDeviceStore, ServerRefused } from './fakeKeysServer'
import { forgetKept } from './release'
import {
  type FlowDeps,
  KeysMismatchError,
  SetUpIncompleteError,
  addGeneration,
  catchUp,
  isWrongPassphrase,
  loadState,
  resetEncryption,
  setUp,
  unlock,
} from './flows'

// The keys of a person changing: a new generation of the account key when a device was lost, a device that is behind it, and starting
// over. The real cryptography against a fake server that follows the rules of the real one.

const USER = '3f2a9c1e-7b4d-4e8a-9c55-0a1b2c3d4e5f'
const PASSPHRASE = 'correct horse battery staple'
const NEW_PASSPHRASE = 'a completely different passphrase'

function browser(server: FakeKeysServer, name = 'Chrome on Windows') {
  const store = new MemoryDeviceStore()
  const deps: FlowDeps = {
    api: server,
    store,
    device: { kind: 'Web', name },
    wait: async () => undefined,
    forgetConversationKeys: (userId) =>
      forgetKept({ epochKeys: new MemoryEpochKeyStore(), pins: new MemoryPinStore(), messages: new MemoryLocalStore() }, userId),
  }
  return { deps, store }
}

async function rejection(promise: Promise<unknown>): Promise<unknown> {
  return promise.then(
    () => {
      throw new Error('Expected the promise to reject.')
    },
    (failure: unknown) => failure,
  )
}

// A person with two browsers that hold the keys, as after linking.
async function twoDevices() {
  const server = new FakeKeysServer()
  const first = browser(server, 'Laptop')
  const second = browser(server, 'Phone')
  const a = await setUp(first.deps, USER, PASSPHRASE)
  const b = await unlock(second.deps, USER, PASSPHRASE)
  return { server, first, second, a, b }
}

const generationsOf = (server: FakeKeysServer) => server.bundles.map((entry) => entry.generation)

describe('a new generation of the account key', () => {
  it('is the next generation, from the same identity, kept here and in the backup under the same passphrase', async () => {
    const { server, first, a } = await twoDevices()

    const ready = await addGeneration(first.deps, USER, a, PASSPHRASE)

    expect(ready.deviceId).toBe(a.deviceId)
    expect(ready.secrets.accounts.map((key) => key.generation)).toEqual([1, 2])
    expect(equal(ready.secrets.identity.publicKey, a.secrets.identity.publicKey)).toBe(true)
    expect(generationsOf(server)).toEqual([1, 2])
    const published = await openBundle(USER, fromBase64Url(server.bundles[1].bundle))
    expect(equal(published.identityPublic, a.secrets.identity.publicKey)).toBe(true)
    expect(equal(published.accountPublic, ready.secrets.accounts[1].publicKey)).toBe(true)
    expect(server.backup?.version).toBe(2)
    const opened = await openBackup(USER, PASSPHRASE, fromBase64Url(server.backup!.blob))
    expect(toHex(encodeSecrets(opened))).toBe(toHex(encodeSecrets(ready.secrets)))
    expect(toHex((await first.store.load(USER))!.secrets)).toBe(toHex(encodeSecrets(ready.secrets)))
  })

  it('removes the device that was lost in the same step and leaves the others', async () => {
    const { server, first, a, b } = await twoDevices()

    await addGeneration(first.deps, USER, a, PASSPHRASE, b.deviceId)

    expect(server.devices.map((device) => device.id)).toEqual([a.deviceId])
  })

  it('asks the passphrase, and a wrong one posts nothing and keeps the keys as they were', async () => {
    const { server, first, a } = await twoDevices()
    const before = toHex((await first.store.load(USER))!.secrets)

    const failure = await rejection(addGeneration(first.deps, USER, a, 'not the passphrase at all'))

    expect(isWrongPassphrase(failure)).toBe(true)
    expect(server.calls).not.toContain('addGeneration')
    expect(generationsOf(server)).toEqual([1])
    expect(toHex((await first.store.load(USER))!.secrets)).toBe(before)
  })

  it('is not made from keys that are behind the server: the other device already made one', async () => {
    const { server, first, second, a, b } = await twoDevices()
    await addGeneration(first.deps, USER, a, PASSPHRASE)

    const failure = await rejection(addGeneration(second.deps, USER, b, PASSPHRASE))

    expect(failure).toBeInstanceOf(KeysMismatchError)
    expect(generationsOf(server)).toEqual([1, 2])
    expect(server.backup?.version).toBe(2)
  })

  it('leaves the keys of this browser as they were when the server refuses', async () => {
    const { server, first, a } = await twoDevices()
    const before = toHex((await first.store.load(USER))!.secrets)
    server.failNext.set('addGeneration', new ServerRefused(409))

    const failure = await rejection(addGeneration(first.deps, USER, a, PASSPHRASE))

    expect(failure).toBeInstanceOf(ServerRefused)
    expect(toHex((await first.store.load(USER))!.secrets)).toBe(before)
    expect(generationsOf(server)).toEqual([1])
  })

  it('is found by the next load when the server took it and this browser could not keep it, and the passphrase mends it', async () => {
    const { server, first, a } = await twoDevices()
    first.store.failNextSave = new Error('quota')

    const failure = await rejection(addGeneration(first.deps, USER, a, PASSPHRASE))

    expect((failure as Error).message).toBe('quota')
    expect(generationsOf(server)).toEqual([1, 2])
    const loaded = await loadState(first.deps, USER)
    expect(loaded.state).toEqual({ phase: 'outdated', deviceId: a.deviceId })

    const mended = await catchUp(first.deps, USER, a.deviceId, PASSPHRASE)
    expect(mended.secrets.accounts.map((key) => key.generation)).toEqual([1, 2])
  })
})

describe('a device that is behind', () => {
  it('is outdated when the server has a newer generation than the keys it holds, and ready when it has the newest', async () => {
    const { first, second, a } = await twoDevices()
    expect((await loadState(second.deps, USER)).state.phase).toBe('ready')

    await addGeneration(first.deps, USER, a, PASSPHRASE)

    const loaded = await loadState(second.deps, USER)
    expect(loaded.removedElsewhere).toBe(false)
    expect(loaded.state).toMatchObject({ phase: 'outdated' })
    expect((await loadState(first.deps, USER)).state.phase).toBe('ready')
  })

  it('catches up with the passphrase, as the device it already is and with no new registration', async () => {
    const { server, first, second, a, b } = await twoDevices()
    await addGeneration(first.deps, USER, a, PASSPHRASE)
    const registered = server.calls.filter((call) => call === 'registerDevice').length

    const ready = await catchUp(second.deps, USER, b.deviceId, PASSPHRASE)

    expect(ready.deviceId).toBe(b.deviceId)
    expect(ready.secrets.accounts.map((key) => key.generation)).toEqual([1, 2])
    expect(server.calls.filter((call) => call === 'registerDevice')).toHaveLength(registered)
    expect(server.devices).toHaveLength(2)
    expect((await loadState(second.deps, USER)).state.phase).toBe('ready')
    expect(decodeSecrets((await second.store.load(USER))!.secrets).accounts).toHaveLength(2)
  })

  it('stays as it was with a wrong passphrase', async () => {
    const { first, second, a, b } = await twoDevices()
    await addGeneration(first.deps, USER, a, PASSPHRASE)
    const before = toHex((await second.store.load(USER))!.secrets)

    const failure = await rejection(catchUp(second.deps, USER, b.deviceId, 'not the passphrase at all'))

    expect(isWrongPassphrase(failure)).toBe(true)
    expect(toHex((await second.store.load(USER))!.secrets)).toBe(before)
  })

  it('refuses a backup that lacks the newest generation the server has', async () => {
    const { server, first, second, a, b } = await twoDevices()
    await addGeneration(first.deps, USER, a, PASSPHRASE)
    // A server that lists a generation the backup does not hold.
    server.bundles.push({ generation: 3, bundle: server.bundles[1].bundle })

    const failure = await rejection(catchUp(second.deps, USER, b.deviceId, PASSPHRASE))

    expect(failure).toBeInstanceOf(KeysMismatchError)
    expect(decodeSecrets((await second.store.load(USER))!.secrets).accounts).toHaveLength(1)
  })

  it('refuses keys whose oldest generation is not the one the server holds the public half of', async () => {
    const { server, first, second, a, b } = await twoDevices()
    await addGeneration(first.deps, USER, a, PASSPHRASE)
    server.bundles[0] = { generation: 1, bundle: server.bundles[1].bundle }

    expect(await rejection(catchUp(second.deps, USER, b.deviceId, PASSPHRASE))).toBeInstanceOf(KeysMismatchError)
  })

  it('refuses a backup whose keys are not the ones the server holds the public halves of', async () => {
    const { server, first, second, a, b } = await twoDevices()
    await addGeneration(first.deps, USER, a, PASSPHRASE)
    server.bundles[1] = { generation: 2, bundle: server.bundles[0].bundle }

    expect(await rejection(catchUp(second.deps, USER, b.deviceId, PASSPHRASE))).toBeInstanceOf(KeysMismatchError)
  })
})

describe('starting over', () => {
  it('makes new keys as the generation after the newest, under a new passphrase, and this browser is the only device', async () => {
    const { server, a } = await twoDevices()
    const fresh = browser(server, 'New browser')

    const ready = await resetEncryption(fresh.deps, USER, NEW_PASSPHRASE, server.password)

    expect(generationsOf(server)).toEqual([1, 2])
    expect(ready.secrets.accounts.map((key) => key.generation)).toEqual([2])
    expect(equal(ready.secrets.identity.publicKey, a.secrets.identity.publicKey)).toBe(false)
    const published = await openBundle(USER, fromBase64Url(server.bundles[1].bundle))
    expect(equal(published.identityPublic, ready.secrets.identity.publicKey)).toBe(true)
    expect(server.devices.map((device) => device.id)).toEqual([ready.deviceId])
    expect(toHex((await fresh.store.load(USER))!.secrets)).toBe(toHex(encodeSecrets(ready.secrets)))
  })

  it('replaces the backup: the new passphrase opens the new keys and the old one opens nothing', async () => {
    const { server } = await twoDevices()
    await resetEncryption(browser(server).deps, USER, NEW_PASSPHRASE, server.password)

    const opened = await openBackup(USER, NEW_PASSPHRASE, fromBase64Url(server.backup!.blob))
    expect(opened.accounts.map((key) => key.generation)).toEqual([2])
    expect(isWrongPassphrase(await rejection(openBackup(USER, PASSPHRASE, fromBase64Url(server.backup!.blob))))).toBe(true)
  })

  it('can be unlocked on another browser afterwards, although the bundles before it belong to the old keys', async () => {
    const { server } = await twoDevices()
    const reset = await resetEncryption(browser(server).deps, USER, NEW_PASSPHRASE, server.password)

    const other = await unlock(browser(server, 'Another browser').deps, USER, NEW_PASSPHRASE)

    expect(toHex(encodeSecrets(other.secrets))).toBe(toHex(encodeSecrets(reset.secrets)))
  })

  it('refuses keys made by a reset whose first generation is not the one the server holds the public half of', async () => {
    const { server } = await twoDevices()
    await resetEncryption(browser(server).deps, USER, NEW_PASSPHRASE, server.password)
    server.bundles[1] = { generation: 2, bundle: server.bundles[0].bundle }

    expect(await rejection(unlock(browser(server, 'Another browser').deps, USER, NEW_PASSPHRASE))).toBeInstanceOf(KeysMismatchError)
  })

  it('does not accept the old backup after it: the keys that open it are not the ones the server holds', async () => {
    const { server } = await twoDevices()
    const oldBackup = { ...server.backup! }
    await resetEncryption(browser(server).deps, USER, NEW_PASSPHRASE, server.password)
    server.backup = { blob: oldBackup.blob, version: 9 }

    expect(await rejection(unlock(browser(server).deps, USER, PASSPHRASE))).toBeInstanceOf(KeysMismatchError)
  })

  it('stores nothing when the password is wrong', async () => {
    const { server } = await twoDevices()
    const fresh = browser(server)

    const failure = await rejection(resetEncryption(fresh.deps, USER, NEW_PASSPHRASE, 'not the password'))

    expect(failure).toBeInstanceOf(ServerRefused)
    expect((failure as ServerRefused).status).toBe(403)
    expect(generationsOf(server)).toEqual([1])
    expect(server.devices).toHaveLength(2)
    expect(await fresh.store.load(USER)).toBeNull()
  })

  it('refuses a passphrase the backup would refuse before anything reaches the server except the question of which generation is next', async () => {
    const { server } = await twoDevices()

    const failure = await rejection(resetEncryption(browser(server).deps, USER, 'too short', server.password))

    expect(failure).toBeInstanceOf(CryptoError)
    expect(server.calls).not.toContain('resetKeys')
  })

  it('says so when the keys were made but this browser could not be added as a device, and the new passphrase finishes it', async () => {
    const { server } = await twoDevices()
    const fresh = browser(server)
    server.failNext.set('registerDevice', new Error('network'))

    const failure = await rejection(resetEncryption(fresh.deps, USER, NEW_PASSPHRASE, server.password))

    expect(failure).toBeInstanceOf(SetUpIncompleteError)
    expect(generationsOf(server)).toEqual([1, 2])
    expect((await loadState(fresh.deps, USER)).state.phase).toBe('locked')
    const ready = await unlock(fresh.deps, USER, NEW_PASSPHRASE)
    expect(ready.secrets.accounts.map((key) => key.generation)).toEqual([2])
  })
})
