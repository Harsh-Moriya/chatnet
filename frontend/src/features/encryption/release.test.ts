import { describe, expect, it } from 'vitest'
import { FakeKeysServer, MemoryDeviceStore } from './fakeKeysServer'
import { type FlowDeps, loadState, setUp, unlock } from './flows'
import { release } from './release'

const USER = '3f2a9c1e-7b4d-4e8a-9c55-0a1b2c3d4e5f'
const PASSPHRASE = 'correct horse battery staple'

function browser(server: FakeKeysServer) {
  const store = new MemoryDeviceStore()
  const deps: FlowDeps = {
    api: server,
    store,
    device: { kind: 'Web', name: 'Chrome on Windows' },
    wait: async () => undefined,
  }
  return { deps, store }
}

describe('signing out', () => {
  it('takes this browser off the server and forgets the keys', async () => {
    const server = new FakeKeysServer()
    const a = browser(server)
    await setUp(a.deps, USER, PASSPHRASE)

    await release(a.deps, USER)

    expect(server.devices).toEqual([])
    expect(await a.store.load(USER)).toBeNull()
    expect(await a.store.lastDeviceId()).toBeNull()
  })

  it('forgets the keys even when the server cannot be reached', async () => {
    const server = new FakeKeysServer()
    const a = browser(server)
    await setUp(a.deps, USER, PASSPHRASE)
    server.failNext.set('removeDevice', new Error('offline'))

    await release(a.deps, USER)

    expect(await a.store.load(USER)).toBeNull()
    expect(server.devices).toHaveLength(1)
  })

  it('does nothing for a browser that holds no keys', async () => {
    const server = new FakeKeysServer()
    const a = browser(server)

    await release(a.deps, USER)

    expect(server.calls).toEqual([])
  })

  it('leaves the other devices of the person and their keys alone', async () => {
    const server = new FakeKeysServer()
    const a = browser(server)
    const b = browser(server)
    await setUp(a.deps, USER, PASSPHRASE)
    await unlock(b.deps, USER, PASSPHRASE)

    await release(a.deps, USER)

    expect(server.devices).toHaveLength(1)
    expect(await b.store.load(USER)).not.toBeNull()
    expect((await loadState(b.deps, USER)).state.phase).toBe('ready')
  })
})
