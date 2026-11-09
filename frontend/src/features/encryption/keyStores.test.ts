import { IDBFactory, IDBKeyRange } from 'fake-indexeddb'
import { beforeEach, describe, expect, it } from 'vitest'
import { indexedDbDeviceStore } from './deviceStore'
import { indexedDbEpochKeyStore as epochKeys } from './epochKeyStore'
import { DATABASE } from './idb'
import { indexedDbPinStore as pins } from './pinStore'

// A fresh database for every test, as a new browser profile would be.
beforeEach(() => {
  globalThis.indexedDB = new IDBFactory()
  globalThis.IDBKeyRange = IDBKeyRange
})

const ANA = '3f2a9c1e-7b4d-4e8a-9c55-0a1b2c3d4e5f'
const BEN = '9d8c7b6a-5e4f-4a3b-8c2d-1e0f9a8b7c6d'
const ROOM = 'c1c1c1c1-0000-4000-8000-000000000001'
const OTHER_ROOM = 'c2c2c2c2-0000-4000-8000-000000000002'

const bytes = (length: number, seed: number) => Uint8Array.from({ length }, (_, i) => (i * 7 + seed) % 256)

describe('the keys of conversations kept in this browser', () => {
  it('keeps a key byte for byte and finds nothing for an epoch that was not saved', async () => {
    const key = bytes(32, 5)

    await epochKeys.save(ANA, ROOM, 3, key)

    expect(Array.from((await epochKeys.load(ANA, ROOM, 3))!)).toEqual(Array.from(key))
    expect(await epochKeys.load(ANA, ROOM, 4)).toBeNull()
    expect(await epochKeys.load(ANA, OTHER_ROOM, 3)).toBeNull()
    expect(await epochKeys.load(BEN, ROOM, 3)).toBeNull()
  })

  it('knows the newest epoch held of each conversation, and 0 for none', async () => {
    await epochKeys.save(ANA, ROOM, 1, bytes(32, 1))
    await epochKeys.save(ANA, ROOM, 9, bytes(32, 2))
    await epochKeys.save(ANA, ROOM, 10, bytes(32, 3))
    await epochKeys.save(ANA, OTHER_ROOM, 2, bytes(32, 4))
    await epochKeys.save(BEN, ROOM, 50, bytes(32, 5))

    expect(await epochKeys.highest(ANA, ROOM)).toBe(10)
    expect(await epochKeys.highest(ANA, OTHER_ROOM)).toBe(2)
    expect(await epochKeys.highest(BEN, ROOM)).toBe(50)
    expect(await epochKeys.highest(BEN, OTHER_ROOM)).toBe(0)
    expect(await epochKeys.highest(ANA, 'a conversation nobody has')).toBe(0)
  })

  it('replaces a key saved again for the same epoch', async () => {
    await epochKeys.save(ANA, ROOM, 1, bytes(32, 1))
    await epochKeys.save(ANA, ROOM, 1, bytes(32, 2))

    expect(Array.from((await epochKeys.load(ANA, ROOM, 1))!)).toEqual(Array.from(bytes(32, 2)))
  })

  it('forgets everything of one person and nothing of anyone else', async () => {
    await epochKeys.save(ANA, ROOM, 1, bytes(32, 1))
    await epochKeys.save(ANA, OTHER_ROOM, 7, bytes(32, 2))
    await epochKeys.save(BEN, ROOM, 1, bytes(32, 3))

    await epochKeys.removeAll(ANA)

    expect(await epochKeys.load(ANA, ROOM, 1)).toBeNull()
    expect(await epochKeys.load(ANA, OTHER_ROOM, 7)).toBeNull()
    expect(await epochKeys.highest(ANA, ROOM)).toBe(0)
    expect(await epochKeys.load(BEN, ROOM, 1)).not.toBeNull()
  })

  it('forgets nothing for a person who has nothing', async () => {
    await expect(epochKeys.removeAll('nobody')).resolves.toBeUndefined()
  })
})

describe('who was seen, kept in this browser', () => {
  it('keeps the identity key of a contact and the time they were first seen', async () => {
    await pins.save({ ownerId: ANA, contactId: BEN, identityPublic: bytes(32, 1), firstSeenAt: '2026-11-08T10:00:00.000Z' })

    const kept = await pins.load(ANA, BEN)

    expect(Array.from(kept!.identityPublic)).toEqual(Array.from(bytes(32, 1)))
    expect(kept!.firstSeenAt).toBe('2026-11-08T10:00:00.000Z')
    expect(await pins.load(BEN, ANA)).toBeNull()
  })

  it('forgets everything one person has seen and nothing another has', async () => {
    await pins.save({ ownerId: ANA, contactId: BEN, identityPublic: bytes(32, 1), firstSeenAt: 'then' })
    await pins.save({ ownerId: ANA, contactId: 'cy', identityPublic: bytes(32, 2), firstSeenAt: 'then' })
    await pins.save({ ownerId: BEN, contactId: ANA, identityPublic: bytes(32, 3), firstSeenAt: 'then' })

    await pins.removeAll(ANA)

    expect(await pins.load(ANA, BEN)).toBeNull()
    expect(await pins.load(ANA, 'cy')).toBeNull()
    expect(await pins.load(BEN, ANA)).not.toBeNull()
  })
})

describe('a browser that had the first version of the database', () => {
  // What the first version made: the device keys, and nothing else.
  function openFirstVersion(): Promise<void> {
    return new Promise((resolve, reject) => {
      const request = indexedDB.open(DATABASE, 1)
      request.onupgradeneeded = () => {
        request.result.createObjectStore('devices', { keyPath: 'userId' })
        request.result.createObjectStore('meta')
      }
      request.onsuccess = () => {
        const db = request.result
        const tx = db.transaction(['devices', 'meta'], 'readwrite')
        tx.objectStore('devices').put({ userId: ANA, deviceId: 'device-a', secrets: Uint8Array.of(1, 2, 3) })
        tx.objectStore('meta').put(ANA, 'lastUserId')
        tx.oncomplete = () => {
          db.close()
          resolve()
        }
        tx.onerror = () => reject(tx.error)
      }
      request.onerror = () => reject(request.error)
    })
  }

  it('keeps the keys it had and gains the new stores', async () => {
    await openFirstVersion()

    const device = await indexedDbDeviceStore.load(ANA)
    await epochKeys.save(ANA, ROOM, 1, bytes(32, 9))
    await pins.save({ ownerId: ANA, contactId: BEN, identityPublic: bytes(32, 8), firstSeenAt: 'then' })

    expect(device?.deviceId).toBe('device-a')
    expect(Array.from(device!.secrets)).toEqual([1, 2, 3])
    expect(await indexedDbDeviceStore.lastDeviceId()).toBe('device-a')
    expect(await epochKeys.highest(ANA, ROOM)).toBe(1)
    expect(await pins.load(ANA, BEN)).not.toBeNull()
  })
})

describe('a browser that cannot keep them', () => {
  it('rejects instead of pretending, when there is no IndexedDB', async () => {
    // @ts-expect-error a browser without IndexedDB has no such global
    globalThis.indexedDB = undefined

    await expect(epochKeys.load(ANA, ROOM, 1)).rejects.toThrow('IndexedDB is not available')
    await expect(epochKeys.save(ANA, ROOM, 1, bytes(32, 1))).rejects.toThrow()
    await expect(pins.load(ANA, BEN)).rejects.toThrow('IndexedDB is not available')
  })
})
