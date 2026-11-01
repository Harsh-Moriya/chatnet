import { AxiosError, type AxiosResponse } from 'axios'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '../../../lib/api'
import { useChatStore } from '../store/chatStore'
import type { Conversation, Member } from '../types'
import { addMembers, changeMemberRole, leaveConversation, removeMember, renameConversation } from './chatApi'

const store = () => useChatStore.getState()

const respond = <T>(data: T) => ({ data }) as AxiosResponse<T>

function member(userId: string, overrides: Partial<Member> = {}): Member {
  return { userId, userName: userId, displayName: userId.toUpperCase(), avatarUrl: null, role: 'Member', lastReadAt: null, ...overrides }
}

function group(overrides: Partial<Conversation> = {}): Conversation {
  return {
    id: 'c1',
    type: 'Group',
    name: 'Team',
    avatarUrl: null,
    updatedAt: '2026-10-11T09:00:00Z',
    lastMessage: null,
    unreadCount: 0,
    otherUserId: null,
    isMuted: false,
    leftAt: null,
    ...overrides,
  }
}

const refusal = (status: number, detail: string) =>
  new AxiosError('Request failed', 'ERR_BAD_REQUEST', undefined, undefined, {
    status,
    data: { title: 'Forbidden', status, detail },
  } as AxiosResponse)

beforeEach(() => {
  store().reset()
  store().setConnection('connected')
  store().seedConversations([group()])
  store().seedMembers('c1', [member('me', { role: 'Owner' }), member('bob')])
})

afterEach(() => {
  vi.restoreAllMocks()
})

describe('adding people to a group', () => {
  it('posts the ids and puts the answer, the people as they are now, in the store', async () => {
    const answer = [member('me', { role: 'Owner' }), member('bob'), member('carol')]
    const post = vi.spyOn(api, 'post').mockResolvedValue(respond(answer))

    const result = await addMembers('c1', ['carol'])

    expect(post).toHaveBeenCalledWith('/conversations/c1/members', { userIds: ['carol'] })
    expect(result).toEqual(answer)
    expect(store().members.c1.map((m) => m.userId)).toEqual(['me', 'bob', 'carol'])
  })

  it('changes nothing and rejects with the reason when it is refused', async () => {
    const before = store().members
    vi.spyOn(api, 'post').mockRejectedValue(refusal(403, 'Only the owner or an admin can add people to this group.'))

    await expect(addMembers('c1', ['carol'])).rejects.toBeInstanceOf(AxiosError)

    expect(store().members).toBe(before)
  })
})

describe('removing someone from a group', () => {
  it('deletes the member and puts the people who are left in the store', async () => {
    const remove = vi.spyOn(api, 'delete').mockResolvedValue(respond([member('me', { role: 'Owner' })]))

    await removeMember('c1', 'bob')

    expect(remove).toHaveBeenCalledWith('/conversations/c1/members/bob')
    expect(store().members.c1.map((m) => m.userId)).toEqual(['me'])
  })

  it('changes nothing when it is refused', async () => {
    const before = store().members
    vi.spyOn(api, 'delete').mockRejectedValue(refusal(403, 'An admin can remove members, not other admins or the owner.'))

    await expect(removeMember('c1', 'bob')).rejects.toBeInstanceOf(AxiosError)

    expect(store().members).toBe(before)
  })
})

describe('changing someone\x27s role', () => {
  it('puts the role to the member and the people as they are now into the store', async () => {
    const put = vi
      .spyOn(api, 'put')
      .mockResolvedValue(respond([member('me', { role: 'Owner' }), member('bob', { role: 'Admin' })]))

    await changeMemberRole('c1', 'bob', 'Admin')

    expect(put).toHaveBeenCalledWith('/conversations/c1/members/bob/role', { role: 'Admin' })
    expect(store().members.c1.find((m) => m.userId === 'bob')?.role).toBe('Admin')
  })

  it('shows a hand-over of the group: the new owner, and the old owner an admin', async () => {
    vi.spyOn(api, 'put').mockResolvedValue(respond([member('me', { role: 'Admin' }), member('bob', { role: 'Owner' })]))

    await changeMemberRole('c1', 'bob', 'Owner')

    expect(store().members.c1.map((m) => [m.userId, m.role])).toEqual([
      ['me', 'Admin'],
      ['bob', 'Owner'],
    ])
  })

  it('changes nothing when it is refused', async () => {
    const before = store().members
    vi.spyOn(api, 'put').mockRejectedValue(refusal(403, 'Only the owner can change roles.'))

    await expect(changeMemberRole('c1', 'bob', 'Admin')).rejects.toBeInstanceOf(AxiosError)

    expect(store().members).toBe(before)
  })
})

describe('leaving a group', () => {
  it('posts the leave and marks the conversation as left at the time the server gives', async () => {
    const post = vi.spyOn(api, 'post').mockResolvedValue(respond({ leftAt: '2026-10-11T12:00:00Z' }))

    await leaveConversation('c1')

    expect(post).toHaveBeenCalledWith('/conversations/c1/leave')
    expect(store().conversations.c1.leftAt).toBe('2026-10-11T12:00:00Z')
    expect(store().members.c1).toBeUndefined()
  })

  it('leaves the conversation as it was when it is refused', async () => {
    const before = store().conversations
    vi.spyOn(api, 'post').mockRejectedValue(refusal(403, 'You are not a member of this conversation.'))

    await expect(leaveConversation('c1')).rejects.toBeInstanceOf(AxiosError)

    expect(store().conversations).toBe(before)
  })
})

describe('renaming a group', () => {
  it('patches the name and puts the answer in the store', async () => {
    const patch = vi.spyOn(api, 'patch').mockResolvedValue(respond({ id: 'c1', name: 'Crew', avatarUrl: null }))

    const result = await renameConversation('c1', 'Crew')

    expect(patch).toHaveBeenCalledWith('/conversations/c1', { name: 'Crew' })
    expect(result.name).toBe('Crew')
    expect(store().conversations.c1.name).toBe('Crew')
  })

  it('keeps the old name when it is refused', async () => {
    vi.spyOn(api, 'patch').mockRejectedValue(refusal(403, 'Only the owner or an admin can rename this group.'))

    await expect(renameConversation('c1', 'Crew')).rejects.toBeInstanceOf(AxiosError)

    expect(store().conversations.c1.name).toBe('Team')
  })
})
