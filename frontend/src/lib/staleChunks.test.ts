import { describe, expect, it, vi } from 'vitest'
import { RELOAD_KEY, RELOAD_WINDOW_MS, reloadForStaleChunk } from './staleChunks'

function setup(stored?: string) {
  const values = new Map<string, string>(stored === undefined ? [] : [[RELOAD_KEY, stored]])
  const storage = {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => void values.set(key, value),
  }
  const reload = vi.fn()
  const event = new Event('vite:preloadError', { cancelable: true })
  return { values, storage, reload, event }
}

describe('a script file that is gone after an update', () => {
  it('reloads the page once and does not report the failed import', () => {
    const { storage, reload, event, values } = setup()

    const took = reloadForStaleChunk(event, { storage: () => storage, now: () => 50_000, reload })

    expect(took).toBe(true)
    expect(reload).toHaveBeenCalledTimes(1)
    expect(event.defaultPrevented).toBe(true)
    expect(values.get(RELOAD_KEY)).toBe('50000')
  })

  it('does not reload again straight away, so a file that is really missing is reported and not looped on', () => {
    const { storage, reload, event } = setup('50000')

    const took = reloadForStaleChunk(event, { storage: () => storage, now: () => 50_000 + RELOAD_WINDOW_MS - 1, reload })

    expect(took).toBe(false)
    expect(reload).not.toHaveBeenCalled()
    expect(event.defaultPrevented).toBe(false)
  })

  it('reloads again for a later update, once the window has passed', () => {
    const { storage, reload, event } = setup('50000')

    expect(reloadForStaleChunk(event, { storage: () => storage, now: () => 50_000 + RELOAD_WINDOW_MS, reload })).toBe(true)
    expect(reload).toHaveBeenCalledTimes(1)
  })

  it('treats a stored value that is not a time as nothing stored', () => {
    const { storage, reload, event } = setup('not a time')

    expect(reloadForStaleChunk(event, { storage: () => storage, now: () => 1_000, reload })).toBe(true)
  })

  it('does not reload in a browser that cannot keep the time, because it could not remember that it did', () => {
    const { reload, event } = setup()
    const failing = {
      getItem: () => null,
      setItem: () => {
        throw new DOMException('full', 'QuotaExceededError')
      },
    }

    expect(reloadForStaleChunk(event, { storage: () => failing, now: () => 50_000, reload })).toBe(false)
    expect(reload).not.toHaveBeenCalled()
    expect(event.defaultPrevented).toBe(false)
  })

  it('does not reload in a browser that blocks the storage altogether', () => {
    const { reload, event } = setup()
    const blocked = () => {
      throw new DOMException('blocked', 'SecurityError')
    }

    expect(reloadForStaleChunk(event, { storage: blocked, now: () => 50_000, reload })).toBe(false)
    expect(reload).not.toHaveBeenCalled()
  })

  it('does not reload in a browser that cannot read the time either', () => {
    const { reload, event } = setup()
    const blocked = {
      getItem: () => {
        throw new DOMException('blocked', 'SecurityError')
      },
      setItem: () => undefined,
    }

    expect(reloadForStaleChunk(event, { storage: () => blocked, now: () => 50_000, reload })).toBe(false)
    expect(reload).not.toHaveBeenCalled()
  })
})
