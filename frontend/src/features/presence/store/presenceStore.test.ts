import { beforeEach, describe, expect, it } from 'vitest'
import type { Presence } from '../types'
import { usePresenceStore } from './presenceStore'

const store = () => usePresenceStore.getState()

function presence(overrides: Partial<Presence> & { userId: string }): Presence {
  return { isOnline: true, lastSeenAt: null, at: '2026-10-11T10:00:00.000Z', ...overrides }
}

beforeEach(() => {
  store().reset()
})

describe('applying a change', () => {
  it('records someone who was not known', () => {
    store().apply(presence({ userId: 'bob' }))

    expect(store().byUser.bob.isOnline).toBe(true)
  })

  it('replaces what it knew with a newer change', () => {
    store().apply(presence({ userId: 'bob', isOnline: true, at: '2026-10-11T10:00:00.000Z' }))

    store().apply(
      presence({ userId: 'bob', isOnline: false, lastSeenAt: '2026-10-11T10:05:00.000Z', at: '2026-10-11T10:05:00.000Z' }),
    )

    expect(store().byUser.bob.isOnline).toBe(false)
  })

  // Two servers announce a refresh (offline, then online) and the messages can arrive the wrong way
  // round. The time on each says which happened last.
  it('ignores a change that arrives after a newer one', () => {
    store().apply(presence({ userId: 'bob', isOnline: true, at: '2026-10-11T10:05:00.000Z' }))

    store().apply(
      presence({ userId: 'bob', isOnline: false, lastSeenAt: '2026-10-11T10:04:59.000Z', at: '2026-10-11T10:04:59.000Z' }),
    )

    expect(store().byUser.bob.isOnline).toBe(true)
  })

  it('compares times by value, not as text', () => {
    store().apply(presence({ userId: 'bob', isOnline: true, at: '2026-10-11T10:00:00.5Z' }))

    store().apply(presence({ userId: 'bob', isOnline: false, at: '2026-10-11T10:00:00Z' }))

    expect(store().byUser.bob.isOnline).toBe(true)
  })

  it('takes a change with exactly the same time', () => {
    store().apply(presence({ userId: 'bob', isOnline: true }))

    store().apply(presence({ userId: 'bob', isOnline: false, lastSeenAt: '2026-10-11T10:00:00.000Z' }))

    expect(store().byUser.bob.isOnline).toBe(false)
  })

  it('keeps people apart', () => {
    store().apply(presence({ userId: 'bob', isOnline: true }))
    store().apply(presence({ userId: 'ann', isOnline: false }))

    expect(store().byUser.bob.isOnline).toBe(true)
    expect(store().byUser.ann.isOnline).toBe(false)
  })

  it('does not tell anyone anything when it changes nothing', () => {
    store().apply(presence({ userId: 'bob', at: '2026-10-11T10:05:00.000Z' }))
    const before = store()

    store().apply(presence({ userId: 'bob', isOnline: false, at: '2026-10-11T10:00:00.000Z' }))

    expect(store()).toBe(before)
  })
})

describe('when they were last seen', () => {
  it('is kept when the person comes back online, because an online change carries none', () => {
    store().apply(
      presence({ userId: 'bob', isOnline: false, lastSeenAt: '2026-10-11T09:00:00.000Z', at: '2026-10-11T09:00:00.000Z' }),
    )

    store().apply(presence({ userId: 'bob', isOnline: true, lastSeenAt: null, at: '2026-10-11T10:00:00.000Z' }))

    expect(store().byUser.bob.lastSeenAt).toBe('2026-10-11T09:00:00.000Z')
  })

  it('is replaced when they go offline again', () => {
    store().apply(presence({ userId: 'bob', isOnline: true, lastSeenAt: '2026-10-11T09:00:00.000Z' }))

    store().apply(
      presence({ userId: 'bob', isOnline: false, lastSeenAt: '2026-10-11T10:30:00.000Z', at: '2026-10-11T10:30:00.000Z' }),
    )

    expect(store().byUser.bob.lastSeenAt).toBe('2026-10-11T10:30:00.000Z')
  })

  it('stays unknown for someone who has never been seen offline', () => {
    store().apply(presence({ userId: 'bob', isOnline: true }))

    expect(store().byUser.bob.lastSeenAt).toBeNull()
  })
})

describe('loading the snapshot', () => {
  it('records everyone in it', () => {
    store().seed([presence({ userId: 'bob' }), presence({ userId: 'ann', isOnline: false })])

    expect(Object.keys(store().byUser).sort()).toEqual(['ann', 'bob'])
  })

  // The snapshot was read at some moment; a push about the same person that happened after it is newer.
  it('does not undo a change that happened after the snapshot was read', () => {
    store().apply(
      presence({ userId: 'bob', isOnline: false, lastSeenAt: '2026-10-11T10:10:00.000Z', at: '2026-10-11T10:10:00.000Z' }),
    )

    store().seed([presence({ userId: 'bob', isOnline: true, at: '2026-10-11T10:05:00.000Z' })])

    expect(store().byUser.bob.isOnline).toBe(false)
  })

  it('replaces what it knew when it is newer', () => {
    store().apply(presence({ userId: 'bob', isOnline: true, at: '2026-10-11T10:00:00.000Z' }))

    store().seed([
      presence({ userId: 'bob', isOnline: false, lastSeenAt: '2026-10-11T10:20:00.000Z', at: '2026-10-11T10:20:00.000Z' }),
    ])

    expect(store().byUser.bob.isOnline).toBe(false)
  })
})

describe('reset', () => {
  it('forgets everyone, so one user never sees who the previous user could see', () => {
    store().apply(presence({ userId: 'bob' }))

    store().reset()

    expect(store().byUser).toEqual({})
  })
})
