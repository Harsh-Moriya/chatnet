import { describe, expect, it } from 'vitest'
import { createKeyedQueue } from './keyedQueue'

// A job that finishes when the test says so.
function gate() {
  let open!: () => void
  const opened = new Promise<void>((resolve) => {
    open = resolve
  })
  return { opened, open }
}

describe('a queue of jobs by key', () => {
  it('runs the jobs of one key in the order they were queued, though a later one would finish first', async () => {
    const queue = createKeyedQueue()
    const slow = gate()
    const order: string[] = []

    const first = queue.run('a', async () => {
      await slow.opened
      order.push('first')
    })
    const second = queue.run('a', async () => {
      order.push('second')
    })
    await Promise.resolve()
    expect(order).toEqual([])

    slow.open()
    await Promise.all([first, second])

    expect(order).toEqual(['first', 'second'])
  })

  it('does not make one key wait for another', async () => {
    const queue = createKeyedQueue()
    const blocked = gate()
    const order: string[] = []

    const a = queue.run('a', async () => {
      await blocked.opened
      order.push('a')
    })
    await queue.run('b', async () => {
      order.push('b')
    })

    expect(order).toEqual(['b'])
    blocked.open()
    await a
    expect(order).toEqual(['b', 'a'])
  })

  it('returns what the job returns', async () => {
    expect(await createKeyedQueue().run('a', async () => 42)).toBe(42)
  })

  it('hands the failure of a job to the one who queued it and runs the jobs behind it', async () => {
    const queue = createKeyedQueue()

    const failed = queue.run('a', async () => {
      throw new Error('broken')
    })
    const behind = queue.run('a', async () => 'still runs')

    await expect(failed).rejects.toThrow('broken')
    expect(await behind).toBe('still runs')
  })

  it('starts again cleanly once a key has run dry', async () => {
    const queue = createKeyedQueue()

    await queue.run('a', async () => 1)
    await Promise.resolve()

    expect(await queue.run('a', async () => 2)).toBe(2)
  })
})
