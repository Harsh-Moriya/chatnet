import { describe, expect, it } from 'vitest'
import { MAX_GROUP_MEMBERS, canManage, memberActions, roleOf, roomLeft } from './groupRules'
import type { Member } from './types'

function member(userId: string, role: Member['role'] = 'Member'): Member {
  return { userId, userName: userId, displayName: userId, avatarUrl: null, role, lastReadAt: null }
}

describe('who may manage a group', () => {
  it('is the owner and the admins, not a member and not someone whose role is not known', () => {
    expect(canManage('Owner')).toBe(true)
    expect(canManage('Admin')).toBe(true)
    expect(canManage('Member')).toBe(false)
    expect(canManage(undefined)).toBe(false)
  })
})

describe('the reader\x27s own role', () => {
  const people = [member('me', 'Admin'), member('bob')]

  it('is found among the people of the group', () => {
    expect(roleOf(people, 'me')).toBe('Admin')
    expect(roleOf(people, 'bob')).toBe('Member')
  })

  it('is not known while the people are not loaded, or when the reader is not among them', () => {
    expect(roleOf(undefined, 'me')).toBeUndefined()
    expect(roleOf(people, 'carol')).toBeUndefined()
    expect(roleOf(people, undefined)).toBeUndefined()
  })
})

describe('what is offered for another member', () => {
  it('lets the owner promote a member or demote an admin, hand the group over, or remove them', () => {
    expect(memberActions('Owner', member('bob'), 'me')).toEqual(['makeAdmin', 'makeOwner', 'remove'])
    expect(memberActions('Owner', member('bob', 'Admin'), 'me')).toEqual(['makeMember', 'makeOwner', 'remove'])
  })

  it('lets an admin remove a plain member and nothing else', () => {
    expect(memberActions('Admin', member('bob'), 'me')).toEqual(['remove'])
    expect(memberActions('Admin', member('bob', 'Admin'), 'me')).toEqual([])
    expect(memberActions('Admin', member('bob', 'Owner'), 'me')).toEqual([])
  })

  it('offers a plain member, and a reader whose role is not known, nothing', () => {
    expect(memberActions('Member', member('bob'), 'me')).toEqual([])
    expect(memberActions(undefined, member('bob'), 'me')).toEqual([])
  })

  it('offers nobody anything for themselves, whatever their role', () => {
    expect(memberActions('Owner', member('me', 'Owner'), 'me')).toEqual([])
    expect(memberActions('Admin', member('me', 'Admin'), 'me')).toEqual([])
  })
})

describe('room in a group', () => {
  it('is what is left of the limit, and never below nothing', () => {
    expect(roomLeft(3)).toBe(MAX_GROUP_MEMBERS - 3)
    expect(roomLeft(MAX_GROUP_MEMBERS)).toBe(0)
    expect(roomLeft(MAX_GROUP_MEMBERS + 5)).toBe(0)
  })
})
