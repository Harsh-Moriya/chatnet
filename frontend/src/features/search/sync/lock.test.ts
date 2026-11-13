import { afterEach, describe, expect, it, vi } from 'vitest'
import { withReadLock } from './lock'

afterEach(() => {
  vi.unstubAllGlobals()
})

// A browser's lock manager that holds the names it was asked for while their work runs.
function lockManager(held: Set<string> = new Set()) {
  const requests: { name: string; options: unknown }[] = []
  const request = vi.fn(async (name: string, options: { ifAvailable: boolean }, work: (lock: object | null) => Promise<unknown>) => {
    requests.push({ name, options })
    if (held.has(name)) return work(null)

    held.add(name)
    try {
      return await work({})
    } finally {
      held.delete(name)
    }
  })
  return { request, requests, held }
}

describe('one tab reads for a person at a time', () => {
  it('does the work under a lock of the person, taken only if no other tab holds it', async () => {
    const locks = lockManager()
    vi.stubGlobal('navigator', { locks })
    const work = vi.fn().mockResolvedValue('read')

    expect(await withReadLock('ana', work)).toBe('read')

    expect(work).toHaveBeenCalledOnce()
    expect(locks.requests).toEqual([{ name: 'chatnet-local-messages:ana', options: { ifAvailable: true } }])
  })

  it('does not do the work when another tab holds the lock, and says so', async () => {
    const locks = lockManager(new Set(['chatnet-local-messages:ana']))
    vi.stubGlobal('navigator', { locks })
    const work = vi.fn()

    expect(await withReadLock('ana', work)).toBeNull()

    expect(work).not.toHaveBeenCalled()
  })

  it('does not hold one person back for the work of another', async () => {
    const locks = lockManager(new Set(['chatnet-local-messages:ana']))
    vi.stubGlobal('navigator', { locks })
    const work = vi.fn().mockResolvedValue('read')

    expect(await withReadLock('ben', work)).toBe('read')
  })

  it('goes ahead where the browser has no lock manager', async () => {
    vi.stubGlobal('navigator', {})
    const work = vi.fn().mockResolvedValue('read')

    expect(await withReadLock('ana', work)).toBe('read')
  })
})
