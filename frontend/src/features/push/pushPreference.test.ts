import { describe, expect, it } from 'vitest'
import { type PreferenceStorage, rememberWantsAlerts, wantsAlerts } from './pushPreference'

function memoryStorage(): PreferenceStorage & { entries: Map<string, string> } {
  const entries = new Map<string, string>()
  return {
    entries,
    getItem: (key) => entries.get(key) ?? null,
    setItem: (key, value) => void entries.set(key, value),
    removeItem: (key) => void entries.delete(key),
  }
}

const unavailable: PreferenceStorage = {
  getItem: () => {
    throw new Error('blocked')
  },
  setItem: () => {
    throw new Error('blocked')
  },
  removeItem: () => {
    throw new Error('blocked')
  },
}

describe('whether a person wants alerts in this browser', () => {
  it('is no until they say yes', () => {
    expect(wantsAlerts('alice', memoryStorage())).toBe(false)
  })

  it('is yes after they say yes, and no again after they say no', () => {
    const storage = memoryStorage()

    rememberWantsAlerts('alice', true, storage)
    expect(wantsAlerts('alice', storage)).toBe(true)

    rememberWantsAlerts('alice', false, storage)
    expect(wantsAlerts('alice', storage)).toBe(false)
    expect(storage.entries.size).toBe(0)
  })

  // The browser is shared: what one person chose says nothing about the next.
  it('is kept for each person apart', () => {
    const storage = memoryStorage()

    rememberWantsAlerts('alice', true, storage)

    expect(wantsAlerts('alice', storage)).toBe(true)
    expect(wantsAlerts('bob', storage)).toBe(false)
  })

  it('is no when the stored value is anything but the one this writes', () => {
    const storage = memoryStorage()
    storage.setItem('chatnet-push:alice', 'true')

    expect(wantsAlerts('alice', storage)).toBe(false)
  })

  it('is no, and does not fail, where storage is not available', () => {
    expect(wantsAlerts('alice', unavailable)).toBe(false)
    expect(wantsAlerts('alice', null)).toBe(false)
    expect(() => rememberWantsAlerts('alice', true, unavailable)).not.toThrow()
    expect(() => rememberWantsAlerts('alice', true, null)).not.toThrow()
  })
})
