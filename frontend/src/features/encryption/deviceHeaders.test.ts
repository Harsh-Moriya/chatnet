import { afterEach, describe, expect, it } from 'vitest'
import { deviceHeaders, setDeviceIdProvider } from '../auth/session'

// What a sign-in and a renewal send to say which device of the person this browser is.
afterEach(() => {
  setDeviceIdProvider(() => Promise.resolve(null))
})

describe('deviceHeaders', () => {
  it('sends nothing until a device is known', async () => {
    expect(await deviceHeaders()).toEqual({})
  })

  it('names the device the provider gives, in the header the server reads', async () => {
    setDeviceIdProvider(() => Promise.resolve('3f2a9c1e-7b4d-4e8a-9c55-0a1b2c3d4e5f'))

    expect(await deviceHeaders()).toEqual({ 'X-Device-Id': '3f2a9c1e-7b4d-4e8a-9c55-0a1b2c3d4e5f' })
  })

  it('asks again every time, so a device that was just added is named by the next request', async () => {
    let id: string | null = null
    setDeviceIdProvider(() => Promise.resolve(id))

    expect(await deviceHeaders()).toEqual({})
    id = 'device-1'
    expect(await deviceHeaders()).toEqual({ 'X-Device-Id': 'device-1' })
  })

  // A browser that cannot read its store must still sign in and renew: the header is a convenience, never a requirement.
  it('sends nothing when the device cannot be read', async () => {
    setDeviceIdProvider(() => Promise.reject(new Error('IndexedDB is blocked')))

    expect(await deviceHeaders()).toEqual({})
  })
})
