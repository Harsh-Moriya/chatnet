import { IDBFactory } from 'fake-indexeddb'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { indexedDbDeviceStore } from './deviceStore'
import { signOutDeletesKeys } from './signOutCheck'

// Whether signing out here would delete keys decides if the person is asked first, so it has to be right in both directions: a browser
// that holds keys must not sign out unasked, and one that holds none must not ask.

const ANA = '3f2a9c1e-7b4d-4e8a-9c55-0a1b2c3d4e5f'
const BEN = '9d8c7b6a-5e4f-4a3b-8c2d-1e0f9a8b7c6d'

beforeEach(() => {
  globalThis.indexedDB = new IDBFactory()
})

describe('whether signing out would delete keys', () => {
  it('does not for a browser that holds none', async () => {
    expect(await signOutDeletesKeys(ANA)).toBe(false)
  })

  it('does for a browser that holds the keys of this person', async () => {
    await indexedDbDeviceStore.save({ userId: ANA, deviceId: 'device', secrets: Uint8Array.of(1, 2, 3) })

    expect(await signOutDeletesKeys(ANA)).toBe(true)
  })

  it('does not for a person whose keys are not here, when another person holds theirs', async () => {
    await indexedDbDeviceStore.save({ userId: ANA, deviceId: 'device', secrets: Uint8Array.of(1, 2, 3) })

    expect(await signOutDeletesKeys(BEN)).toBe(false)
  })

  it('does not when the browser cannot read its store, which holds nothing that signing out could delete', async () => {
    vi.stubGlobal('indexedDB', undefined)

    expect(await signOutDeletesKeys(ANA)).toBe(false)
  })
})
