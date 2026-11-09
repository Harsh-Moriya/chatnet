import { describe, expect, it } from 'vitest'
import { MemoryEpochKeyStore, MemoryPinStore } from './fakeEpochServer'
import { FakeKeysServer, MemoryDeviceStore } from './fakeKeysServer'
import { type FlowDeps, loadState, setUp, unlock } from './flows'
import { forgetKept, release } from './release'

const USER = '3f2a9c1e-7b4d-4e8a-9c55-0a1b2c3d4e5f'
const PASSPHRASE = 'correct horse battery staple'

function browser(server: FakeKeysServer) {
  const store = new MemoryDeviceStore()
  const epochKeys = new MemoryEpochKeyStore()
  const pins = new MemoryPinStore()
  const deps: FlowDeps = {
    api: server,
    store,
    device: { kind: 'Web', name: 'Chrome on Windows' },
    wait: async () => undefined,
    forgetConversationKeys: (userId) => forgetKept({ epochKeys, pins }, userId),
  }
  return { deps, store, epochKeys, pins, leaving: { api: server, store, epochKeys, pins } }
}

describe('signing out', () => {
  it('takes this browser off the server and forgets the keys', async () => {
    const server = new FakeKeysServer()
    const a = browser(server)
    await setUp(a.deps, USER, PASSPHRASE)

    await release(a.leaving, USER)

    expect(server.devices).toEqual([])
    expect(await a.store.load(USER)).toBeNull()
    expect(await a.store.lastDeviceId()).toBeNull()
  })

  it('forgets the keys even when the server cannot be reached', async () => {
    const server = new FakeKeysServer()
    const a = browser(server)
    await setUp(a.deps, USER, PASSPHRASE)
    server.failNext.set('removeDevice', new Error('offline'))

    await release(a.leaving, USER)

    expect(await a.store.load(USER)).toBeNull()
    expect(server.devices).toHaveLength(1)
  })

  it('does nothing for a browser that holds no keys', async () => {
    const server = new FakeKeysServer()
    const a = browser(server)

    await release(a.leaving, USER)

    expect(server.calls).toEqual([])
  })

  it('leaves the other devices of the person and their keys alone', async () => {
    const server = new FakeKeysServer()
    const a = browser(server)
    const b = browser(server)
    await setUp(a.deps, USER, PASSPHRASE)
    await unlock(b.deps, USER, PASSPHRASE)

    await release(a.leaving, USER)

    expect(server.devices).toHaveLength(1)
    expect(await b.store.load(USER)).not.toBeNull()
    expect((await loadState(b.deps, USER)).state.phase).toBe('ready')
  })

  it('forgets the keys of the conversations and who was seen, for this person only', async () => {
    const server = new FakeKeysServer()
    const a = browser(server)
    const OTHER = '9d8c7b6a-5e4f-4a3b-8c2d-1e0f9a8b7c6d'
    await setUp(a.deps, USER, PASSPHRASE)
    await a.epochKeys.save(USER, 'conversation', 1, Uint8Array.of(1))
    await a.epochKeys.save(OTHER, 'conversation', 1, Uint8Array.of(2))
    await a.pins.save({ ownerId: USER, contactId: OTHER, identityPublic: Uint8Array.of(3), firstSeenAt: '2026-11-08T00:00:00.000Z' })
    await a.pins.save({ ownerId: OTHER, contactId: USER, identityPublic: Uint8Array.of(4), firstSeenAt: '2026-11-08T00:00:00.000Z' })

    await release(a.leaving, USER)

    expect(await a.epochKeys.load(USER, 'conversation', 1)).toBeNull()
    expect(await a.pins.load(USER, OTHER)).toBeNull()
    expect(await a.epochKeys.load(OTHER, 'conversation', 1)).not.toBeNull()
    expect(await a.pins.load(OTHER, USER)).not.toBeNull()
  })

  it('forgets them even when this browser holds no keys of the person', async () => {
    const a = browser(new FakeKeysServer())
    await a.epochKeys.save(USER, 'conversation', 1, Uint8Array.of(1))

    await release(a.leaving, USER)

    expect(await a.epochKeys.load(USER, 'conversation', 1)).toBeNull()
  })

  it('forgets them even when the keys of the device cannot be read', async () => {
    const a = browser(new FakeKeysServer())
    await a.epochKeys.save(USER, 'conversation', 1, Uint8Array.of(1))
    a.store.load = () => Promise.reject(new Error('IndexedDB is blocked'))

    await expect(release(a.leaving, USER)).rejects.toThrow('IndexedDB is blocked')

    expect(await a.epochKeys.load(USER, 'conversation', 1)).toBeNull()
  })
})
