import { IDBFactory } from 'fake-indexeddb'
import { beforeEach, describe, expect, it } from 'vitest'
import { canHoldKeys, indexedDbDeviceStore as store } from './deviceStore'

// A fresh database for every test, as a new browser profile would be.
beforeEach(() => {
  globalThis.indexedDB = new IDBFactory()
})

const secrets = (...bytes: number[]) => Uint8Array.from(bytes)

describe('the keys kept in this browser', () => {
  it('keeps what was saved, byte for byte, and finds nothing for a person who has nothing', async () => {
    const bytes = Uint8Array.from({ length: 203 }, (_, i) => (i * 7) % 256)

    await store.save({ userId: 'alice', deviceId: 'device-a', secrets: bytes })

    const loaded = await store.load('alice')
    expect(loaded?.deviceId).toBe('device-a')
    expect(Array.from(loaded!.secrets)).toEqual(Array.from(bytes))
    expect(await store.load('bob')).toBeNull()
  })

  it('keeps people apart and removes only the one asked for', async () => {
    await store.save({ userId: 'alice', deviceId: 'a', secrets: secrets(1) })
    await store.save({ userId: 'bob', deviceId: 'b', secrets: secrets(2) })

    await store.remove('alice')

    expect(await store.load('alice')).toBeNull()
    expect((await store.load('bob'))?.deviceId).toBe('b')
  })

  it('replaces what a person had when they save again', async () => {
    await store.save({ userId: 'alice', deviceId: 'old', secrets: secrets(1) })
    await store.save({ userId: 'alice', deviceId: 'new', secrets: secrets(9) })

    const loaded = await store.load('alice')
    expect(loaded?.deviceId).toBe('new')
    expect(Array.from(loaded!.secrets)).toEqual([9])
  })

  it('removing a person who has nothing is not an error', async () => {
    await expect(store.remove('nobody')).resolves.toBeUndefined()
  })
})

describe('which device a sign-in names', () => {
  it('is none while nobody has keys here', async () => {
    expect(await store.lastDeviceId()).toBeNull()
  })

  it('is the device of whoever saved last', async () => {
    await store.save({ userId: 'alice', deviceId: 'a', secrets: secrets(1) })
    expect(await store.lastDeviceId()).toBe('a')

    await store.save({ userId: 'bob', deviceId: 'b', secrets: secrets(2) })
    expect(await store.lastDeviceId()).toBe('b')
  })

  it('is none once the person it belonged to has signed out, and not another person is guessed', async () => {
    await store.save({ userId: 'alice', deviceId: 'a', secrets: secrets(1) })
    await store.save({ userId: 'bob', deviceId: 'b', secrets: secrets(2) })

    await store.remove('bob')

    expect(await store.lastDeviceId()).toBeNull()
  })

  it('stays when somebody else signs out', async () => {
    await store.save({ userId: 'alice', deviceId: 'a', secrets: secrets(1) })
    await store.save({ userId: 'bob', deviceId: 'b', secrets: secrets(2) })

    await store.remove('alice')

    expect(await store.lastDeviceId()).toBe('b')
  })
})

describe('a browser that cannot keep keys', () => {
  it('rejects instead of pretending, when there is no IndexedDB', async () => {
    // @ts-expect-error a browser without IndexedDB has no such global
    globalThis.indexedDB = undefined

    await expect(store.load('alice')).rejects.toThrow('IndexedDB is not available')
    await expect(store.save({ userId: 'alice', deviceId: 'a', secrets: secrets(1) })).rejects.toThrow()
    expect(await canHoldKeys()).toBe(false)
  })

  it('can hold keys where IndexedDB and the algorithms are there', async () => {
    expect(await canHoldKeys()).toBe(true)
  })
})
