import { beforeEach, describe, expect, it } from 'vitest'
import { useChatStore } from './chatStore'
import type { Conversation, Member, Message } from '../types'

const store = () => useChatStore.getState()

function member(userId: string, overrides: Partial<Member> = {}): Member {
  return { userId, userName: userId, displayName: userId.toUpperCase(), avatarUrl: null, role: 'Member', lastReadAt: null, ...overrides }
}

function group(overrides: Partial<Conversation> & { id: string }): Conversation {
  return {
    type: 'Group',
    name: `Group ${overrides.id}`,
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

function message(id: string, conversationId = 'c1'): Message {
  return {
    id,
    conversationId,
    senderId: 'bob',
    senderDisplayName: 'Bob',
    content: `content of ${id}`,
    createdAt: '2026-10-11T10:00:00Z',
    clientMessageId: null,
    editedAt: null,
    editCount: 0,
    isDeleted: false,
    attachments: [],
  }
}

beforeEach(() => {
  store().reset()
})

// ##### The people of a group change #####

describe('applying a change of the people of a group', () => {
  const seeded = () =>
    store().seedMembers('c1', [member('me', { role: 'Owner' }), member('bob'), member('carol')])

  it('puts a new member at the end and leaves the others where they are', () => {
    seeded()

    store().applyMembersChange({ conversationId: 'c1', members: [member('dave')], removedUserIds: [] })

    expect(store().members.c1.map((m) => m.userId)).toEqual(['me', 'bob', 'carol', 'dave'])
  })

  it('takes out the people who are gone', () => {
    seeded()

    store().applyMembersChange({ conversationId: 'c1', members: [], removedUserIds: ['bob', 'carol'] })

    expect(store().members.c1.map((m) => m.userId)).toEqual(['me'])
  })

  it('puts a member whose role changed in the place they had', () => {
    seeded()

    store().applyMembersChange({ conversationId: 'c1', members: [member('bob', { role: 'Admin' })], removedUserIds: [] })

    expect(store().members.c1.map((m) => [m.userId, m.role])).toEqual([
      ['me', 'Owner'],
      ['bob', 'Admin'],
      ['carol', 'Member'],
    ])
  })

  it('carries a hand-over of the group in one change: the old owner gone and a new owner named', () => {
    seeded()

    store().applyMembersChange({
      conversationId: 'c1',
      members: [member('bob', { role: 'Owner' })],
      removedUserIds: ['me'],
    })

    expect(store().members.c1.map((m) => [m.userId, m.role])).toEqual([
      ['bob', 'Owner'],
      ['carol', 'Member'],
    ])
  })

  it('keeps a read position that is later than the one in the change, and takes one that is later than its own', () => {
    store().seedMembers('c1', [
      member('bob', { lastReadAt: '2026-10-11T10:00:09Z' }),
      member('carol', { lastReadAt: '2026-10-11T10:00:01Z' }),
    ])

    store().applyMembersChange({
      conversationId: 'c1',
      members: [
        member('bob', { role: 'Admin', lastReadAt: '2026-10-11T10:00:02Z' }),
        member('carol', { role: 'Admin', lastReadAt: '2026-10-11T10:00:05Z' }),
      ],
      removedUserIds: [],
    })

    expect(store().members.c1.map((m) => [m.userId, m.lastReadAt])).toEqual([
      ['bob', '2026-10-11T10:00:09Z'],
      ['carol', '2026-10-11T10:00:05Z'],
    ])
  })

  it('ignores a group whose people were never loaded, since opening it loads them as they are', () => {
    const before = store().members

    store().applyMembersChange({ conversationId: 'c1', members: [member('dave')], removedUserIds: [] })

    expect(store().members).toBe(before)
    expect(store().members.c1).toBeUndefined()
  })

  it('changes nothing the second time it is applied', () => {
    seeded()
    const change = { conversationId: 'c1', members: [member('dave')], removedUserIds: ['bob'] }
    store().applyMembersChange(change)
    const after = store().members

    store().applyMembersChange(change)

    expect(store().members).toBe(after)
  })

  it('does not touch another group', () => {
    seeded()
    store().seedMembers('c2', [member('me')])

    store().applyMembersChange({ conversationId: 'c1', members: [], removedUserIds: ['bob'] })

    expect(store().members.c2.map((m) => m.userId)).toEqual(['me'])
  })
})

// ##### A group is renamed #####

describe('applying a rename', () => {
  it('changes the name and the picture of a group', () => {
    store().seedConversations([group({ id: 'c1', name: 'Team' })])

    store().applyConversationUpdate({ id: 'c1', name: 'Crew', avatarUrl: 'https://img.test/crew.png' })

    expect(store().conversations.c1).toMatchObject({ name: 'Crew', avatarUrl: 'https://img.test/crew.png' })
  })

  it('leaves the rest of the conversation as it was', () => {
    store().seedConversations([group({ id: 'c1', unreadCount: 4, isMuted: true })])

    store().applyConversationUpdate({ id: 'c1', name: 'Crew', avatarUrl: null })

    expect(store().conversations.c1).toMatchObject({ unreadCount: 4, isMuted: true, updatedAt: '2026-10-11T09:00:00Z' })
  })

  it('ignores a conversation the store does not know', () => {
    const before = store().conversations

    store().applyConversationUpdate({ id: 'c9', name: 'Crew', avatarUrl: null })

    expect(store().conversations).toBe(before)
  })

  it('never renames a direct chat, whose name is the other person', () => {
    store().seedConversations([group({ id: 'c1', type: 'Direct', name: 'Bob' })])
    const before = store().conversations

    store().applyConversationUpdate({ id: 'c1', name: 'Crew', avatarUrl: null })

    expect(store().conversations).toBe(before)
    expect(store().conversations.c1.name).toBe('Bob')
  })

  it('changes nothing when the name and picture are the same', () => {
    store().seedConversations([group({ id: 'c1', name: 'Team' })])
    const before = store().conversations

    store().applyConversationUpdate({ id: 'c1', name: 'Team', avatarUrl: null })

    expect(store().conversations).toBe(before)
  })
})

// ##### The signed-in user leaves #####

describe('marking a conversation as left', () => {
  it('records when, and leaves nothing unread', () => {
    store().seedConversations([group({ id: 'c1', unreadCount: 5 })])

    store().markLeft('c1', '2026-10-11T12:00:00Z')

    expect(store().conversations.c1).toMatchObject({ leftAt: '2026-10-11T12:00:00Z', unreadCount: 0 })
  })

  it('keeps what was said, so the conversation can still be read', () => {
    store().seedConversations([
      group({ id: 'c1', lastMessage: { senderId: 'bob', content: 'last', createdAt: '2026-10-11T10:00:00Z', attachmentCount: 0 } }),
    ])
    store().seedMessages('c1', [message('m1')])

    store().markLeft('c1', '2026-10-11T12:00:00Z')

    expect(store().messages.c1.map((m) => m.id)).toEqual(['m1'])
    expect(store().conversations.c1.lastMessage?.content).toBe('last')
  })

  it('forgets who is in the group and who is typing in it', () => {
    store().seedConversations([group({ id: 'c1' }), group({ id: 'c2' })])
    store().seedMembers('c1', [member('bob')])
    store().seedMembers('c2', [member('bob')])
    store().setTyping('c1', { userId: 'bob', displayName: 'Bob' }, true)
    store().setTyping('c2', { userId: 'bob', displayName: 'Bob' }, true)

    store().markLeft('c1', '2026-10-11T12:00:00Z')

    expect(store().members.c1).toBeUndefined()
    expect(store().typing.c1).toBeUndefined()
    expect(store().members.c2).toHaveLength(1)
    expect(store().typing.c2).toHaveLength(1)
  })

  it('orders the conversation by when it was left, not by what the others say after', () => {
    store().seedConversations([group({ id: 'c1', updatedAt: '2026-10-11T15:00:00Z' })])

    store().markLeft('c1', '2026-10-11T12:00:00Z')

    expect(store().conversations.c1.updatedAt).toBe('2026-10-11T12:00:00Z')
  })

  it('does not move a conversation whose last activity was before it was left', () => {
    store().seedConversations([group({ id: 'c1', updatedAt: '2026-10-11T09:00:00Z' })])

    store().markLeft('c1', '2026-10-11T12:00:00Z')

    expect(store().conversations.c1.updatedAt).toBe('2026-10-11T09:00:00Z')
  })

  it('is applied once: a second, later report does not move the time', () => {
    store().seedConversations([group({ id: 'c1' })])
    store().markLeft('c1', '2026-10-11T12:00:00Z')
    const after = store().conversations

    store().markLeft('c1', '2026-10-11T13:00:00Z')

    expect(store().conversations).toBe(after)
    expect(store().conversations.c1.leftAt).toBe('2026-10-11T12:00:00Z')
  })

  it('ignores a conversation the store does not know', () => {
    const before = store().conversations

    store().markLeft('c9', '2026-10-11T12:00:00Z')

    expect(store().conversations).toBe(before)
  })
})

// ##### Added back #####

describe('being added back to a group that was left', () => {
  it('replaces the copy that said the user had left, and forgets what was held of the time before', () => {
    store().seedConversations([group({ id: 'c1', leftAt: '2026-10-11T12:00:00Z' }), group({ id: 'c2' })])
    store().seedMessages('c1', [message('m1')])
    store().seedMessages('c2', [message('m2', 'c2')])
    store().seedMembers('c1', [member('bob')])

    store().addConversation(group({ id: 'c1', updatedAt: '2026-10-11T13:00:00Z' }))

    expect(store().conversations.c1.leftAt).toBeNull()
    expect(store().messages.c1).toBeUndefined()
    expect(store().members.c1).toBeUndefined()
    expect(store().messages.c2).toHaveLength(1)
  })

  it('takes the incoming copy even when the one that said the user had left looks fresher', () => {
    store().seedConversations([group({ id: 'c1', leftAt: '2026-10-11T12:00:00Z', updatedAt: '2026-10-11T12:00:00Z' })])

    store().addConversation(group({ id: 'c1', updatedAt: '2026-10-11T09:00:00Z' }))

    expect(store().conversations.c1).toMatchObject({ leftAt: null, updatedAt: '2026-10-11T09:00:00Z' })
  })

  it('does not forget anything when the incoming copy still says the user left', () => {
    store().seedConversations([group({ id: 'c1', leftAt: '2026-10-11T12:00:00Z' })])
    store().seedMessages('c1', [message('m1')])

    store().addConversation(group({ id: 'c1', leftAt: '2026-10-11T12:00:00Z' }))

    expect(store().messages.c1).toHaveLength(1)
  })

  it('does not forget anything for a group the user never left', () => {
    store().seedConversations([group({ id: 'c1' })])
    store().seedMessages('c1', [message('m1')])
    store().seedMembers('c1', [member('bob')])

    store().addConversation(group({ id: 'c1' }))

    expect(store().messages.c1).toHaveLength(1)
    expect(store().members.c1).toHaveLength(1)
  })
})
