import type { AxiosResponse } from 'axios'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '../../../lib/api'
import { usePresenceStore } from '../store/presenceStore'
import type { Presence } from '../types'
import { fetchPresence } from './presenceApi'

const respond = <T>(data: T) => ({ data }) as AxiosResponse<T>

const bob: Presence = { userId: 'bob', isOnline: true, lastSeenAt: null, at: '2026-10-11T10:00:00Z' }

beforeEach(() => {
  usePresenceStore.getState().reset()
})

afterEach(() => {
  vi.restoreAllMocks()
})

describe('loading presence', () => {
  it('asks the server and returns what it said', async () => {
    const get = vi.spyOn(api, 'get').mockResolvedValue(respond([bob]))

    const loaded = await fetchPresence()

    expect(loaded).toEqual([bob])
    expect(get.mock.calls[0][0]).toBe('/presence')
  })

  it('puts the answer in the presence store', async () => {
    vi.spyOn(api, 'get').mockResolvedValue(respond([bob, { ...bob, userId: 'ann', isOnline: false }]))

    await fetchPresence()

    expect(usePresenceStore.getState().byUser.bob.isOnline).toBe(true)
    expect(usePresenceStore.getState().byUser.ann.isOnline).toBe(false)
  })

  it('hands the cancel signal to the request', async () => {
    const get = vi.spyOn(api, 'get').mockResolvedValue(respond([]))
    const controller = new AbortController()

    await fetchPresence(controller.signal)

    expect(get.mock.calls[0][1]).toMatchObject({ signal: controller.signal })
  })

  it('leaves the store alone when the request fails', async () => {
    usePresenceStore.getState().apply(bob)
    vi.spyOn(api, 'get').mockRejectedValue(new Error('Network Error'))

    await expect(fetchPresence()).rejects.toThrow()

    expect(usePresenceStore.getState().byUser.bob.isOnline).toBe(true)
  })

  it('does not undo a change pushed after the snapshot was read', async () => {
    usePresenceStore.getState().apply({ ...bob, isOnline: false, lastSeenAt: '2026-10-11T10:10:00Z', at: '2026-10-11T10:10:00Z' })
    vi.spyOn(api, 'get').mockResolvedValue(respond([{ ...bob, at: '2026-10-11T10:05:00Z' }]))

    await fetchPresence()

    expect(usePresenceStore.getState().byUser.bob.isOnline).toBe(false)
  })
})
