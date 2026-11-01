import { beforeEach, describe, expect, it } from 'vitest'
import type { Conversation, Member, Message, UserSummary } from '../types'
import { useChatStore } from './chatStore'

const store = () => useChatStore.getState()

function conversation(id: string, overrides: Partial<Conversation> = {}): Conversation {
  return {
    id,
    type: 'Direct',
    name: 'Bob',
    avatarUrl: null,
    updatedAt: '2026-10-11T09:00:00Z',
    lastMessage: null,
    unreadCount: 0,
    otherUserId: 'bob',
    isMuted: false,
    leftAt: null,
    ...overrides,
  }
}

function member(userId: string, overrides: Partial<Member> = {}): Member {
  return { userId, userName: userId, displayName: userId.toUpperCase(), avatarUrl: null, role: 'Member', lastReadAt: null, ...overrides }
}

function message(id: string, senderId: string, senderDisplayName: string): Message {
  return {
    id,
    conversationId: 'c1',
    senderId,
    senderDisplayName,
    content: 'hello',
    createdAt: '2026-10-11T10:00:00Z',
    clientMessageId: null,
    editedAt: null,
    editCount: 0,
    isDeleted: false,
    attachments: [],
  }
}

const bob: UserSummary = { id: 'bob', userName: 'bob', displayName: 'Robert', avatarUrl: '/api/avatars/users/bob/f1' }

beforeEach(() => {
  store().reset()
})

describe('a change to someone\'s name or picture', () => {
  it('renames the direct chat that is named after them and gives it their picture', () => {
    store().seedConversations([conversation('c1')])

    store().applyProfileChange(bob)

    expect(store().conversations.c1).toMatchObject({ name: 'Robert', avatarUrl: '/api/avatars/users/bob/f1' })
  })

  it('leaves a group, and a direct chat with someone else, alone', () => {
    store().seedConversations([
      conversation('g1', { type: 'Group', name: 'Team', otherUserId: null }),
      conversation('c2', { name: 'Carol', otherUserId: 'carol' }),
    ])
    const before = store().conversations

    store().applyProfileChange(bob)

    expect(store().conversations).toBe(before)
  })

  it('changes their entry in every list of people it holds, and no one else\'s', () => {
    store().seedMembers('c1', [member('me'), member('bob')])
    store().seedMembers('c2', [member('bob', { role: 'Admin' }), member('carol')])
    store().seedMembers('c3', [member('carol')])
    const untouched = store().members.c3

    store().applyProfileChange(bob)

    expect(store().members.c1[1]).toMatchObject({ displayName: 'Robert', avatarUrl: '/api/avatars/users/bob/f1' })
    expect(store().members.c1[0]).toMatchObject({ displayName: 'ME' })
    expect(store().members.c2[0]).toMatchObject({ displayName: 'Robert', role: 'Admin' })
    expect(store().members.c3).toBe(untouched)
  })

  it('changes the name on the messages they sent and not on anyone else\'s', () => {
    store().seedMessages('c1', [message('m1', 'bob', 'Bob'), message('m2', 'me', 'Me'), message('m3', 'bob', 'Bob')])

    store().applyProfileChange(bob)

    expect(store().messages.c1.map((m) => m.senderDisplayName)).toEqual(['Robert', 'Me', 'Robert'])
  })

  it('is a no-op when everything already agrees, so a repeated push redraws nothing', () => {
    store().seedConversations([conversation('c1', { name: 'Robert', avatarUrl: bob.avatarUrl })])
    store().seedMembers('c1', [member('bob', { displayName: 'Robert', avatarUrl: bob.avatarUrl })])
    store().seedMessages('c1', [message('m1', 'bob', 'Robert')])
    const state = store()

    store().applyProfileChange(bob)

    expect(store()).toBe(state)
  })

  it('takes the picture away again', () => {
    store().seedConversations([conversation('c1', { name: 'Robert', avatarUrl: bob.avatarUrl })])

    store().applyProfileChange({ ...bob, avatarUrl: null })

    expect(store().conversations.c1.avatarUrl).toBeNull()
  })
})
