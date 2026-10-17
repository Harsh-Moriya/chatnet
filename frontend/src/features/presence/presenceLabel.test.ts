import { describe, expect, it } from 'vitest'
import { countOnline, directStatus, groupStatus } from './presenceLabel'
import type { Presence } from './types'

// "Now" is Sunday 11 October 2026, 15:00 local time; times are built in local time so the answers do not
// depend on the time zone.
const NOW = new Date(2026, 9, 11, 15, 0)
const LOCALE = 'en-US'

function presence(overrides: Partial<Presence>): Presence {
  return { userId: 'bob', isOnline: false, lastSeenAt: null, at: '2026-10-11T10:00:00.000Z', ...overrides }
}

describe('the status line of a direct chat', () => {
  it('says nothing while it is not known', () => {
    expect(directStatus(undefined, NOW, LOCALE)).toBeNull()
  })

  it('says Online for someone who is', () => {
    expect(directStatus(presence({ isOnline: true }), NOW, LOCALE)).toBe('Online')
  })

  it('says when someone who is offline was last seen', () => {
    const lastSeenAt = new Date(2026, 9, 11, 14, 55).toISOString()

    expect(directStatus(presence({ lastSeenAt }), NOW, LOCALE)).toBe('Last seen 5 min ago')
  })

  it('says Offline for someone who has never been seen', () => {
    expect(directStatus(presence({ lastSeenAt: null }), NOW, LOCALE)).toBe('Offline')
  })

  it('does not show an old last-seen time for someone who is online again', () => {
    const lastSeenAt = new Date(2026, 9, 11, 9, 0).toISOString()

    expect(directStatus(presence({ isOnline: true, lastSeenAt }), NOW, LOCALE)).toBe('Online')
  })
})

describe('the status line of a group', () => {
  it('counts the members who are online', () => {
    expect(groupStatus(3)).toBe('3 online')
    expect(groupStatus(1)).toBe('1 online')
  })

  it('says nothing when nobody is, so the usual line stays', () => {
    expect(groupStatus(0)).toBeNull()
  })
})

describe('counting who is online', () => {
  const byUser = {
    ann: presence({ userId: 'ann', isOnline: true }),
    bob: presence({ userId: 'bob', isOnline: true }),
    cat: presence({ userId: 'cat', isOnline: false }),
  }

  it('counts the online people among those asked about', () => {
    expect(countOnline(['ann', 'bob', 'cat'], byUser)).toBe(2)
  })

  it('does not count people it knows nothing about', () => {
    expect(countOnline(['ann', 'stranger'], byUser)).toBe(1)
  })

  it('is zero for nobody', () => {
    expect(countOnline([], byUser)).toBe(0)
  })
})
