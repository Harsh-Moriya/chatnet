import { beforeEach, describe, expect, it } from 'vitest'
import { useChatStore } from './chatStore'
import type { CallInfo, Conversation, Member, Message } from '../types'

const store = () => useChatStore.getState()

const missed: CallInfo = {
  id: 'call-1',
  media: 'Audio',
  startedAt: '2026-10-11T10:00:00Z',
  answeredAt: null,
  endedAt: '2026-10-11T10:00:30Z',
  endReason: 'Missed',
  isMissed: true,
}

const answered: CallInfo = { ...missed, answeredAt: '2026-10-11T10:00:05Z', endReason: 'Completed', isMissed: false }

function row(id: string, senderId: string, call: CallInfo, createdAt = call.endedAt): Message {
  return {
    id,
    conversationId: 'c1',
    senderId,
    senderDisplayName: senderId,
    content: null,
    createdAt,
    clientMessageId: null,
    editedAt: null,
    editCount: 0,
    isDeleted: false,
    attachments: [],
    kind: 'Call',
    call,
  }
}

function conversation(unreadCount: number): Conversation {
  return {
    id: 'c1',
    type: 'Direct',
    name: 'Bob',
    avatarUrl: null,
    updatedAt: '2026-10-11T09:00:00Z',
    lastMessage: null,
    unreadCount,
    otherUserId: 'bob',
    isMuted: false,
    leftAt: null,
    keyEpoch: 1,
    keyRotationRequired: false,
    changeSeq: 0,
  }
}

const member = (userId: string, lastReadAt: string | null): Member => ({
  userId,
  userName: userId,
  displayName: userId,
  avatarUrl: null,
  role: 'Member',
  lastReadAt,
})

const unread = () => store().conversations.c1.unreadCount

beforeEach(() => {
  store().reset()
  store().seedConversations([conversation(2)])
})

describe('a call arriving', () => {
  it('is one more unread message for the person who was called when it was missed', () => {
    store().receiveMessage(row('k1', 'bob', missed), 'me')

    expect(unread()).toBe(3)
  })

  it('is not unread when it was answered', () => {
    store().receiveMessage(row('k1', 'bob', answered), 'me')

    expect(unread()).toBe(2)
  })

  it('is not counted twice when the push and a history load both bring it', () => {
    const arrival = row('k1', 'bob', missed)

    store().receiveMessage(arrival, 'me')
    store().receiveMessage(arrival, 'me')

    expect(unread()).toBe(3)
  })

  it('does not clear the unread messages of the person who made it, as sending a message does', () => {
    store().receiveMessage(row('k1', 'me', missed), 'me')

    expect(unread()).toBe(2)
  })

  it('does not move anyone\'s read position, as the server does not', () => {
    store().seedMembers('c1', [member('me', '2026-10-11T09:00:00Z'), member('bob', '2026-10-11T09:30:00Z')])

    store().receiveMessage(row('k1', 'bob', missed), 'me')
    store().receiveMessage(row('k2', 'me', answered, '2026-10-11T10:10:00Z'), 'me')

    expect(store().members.c1.map((m) => m.lastReadAt)).toEqual(['2026-10-11T09:00:00Z', '2026-10-11T09:30:00Z'])
  })

  it('becomes the preview of the conversation', () => {
    store().receiveMessage(row('k1', 'bob', missed), 'me')

    expect(store().conversations.c1.lastMessage).toMatchObject({ senderId: 'bob', kind: 'Call', call: missed })
  })

  it('stays unread after the person reads up to a point before it, and is read after a point beyond it', () => {
    store().receiveMessage(row('k1', 'bob', missed), 'me')

    store().applyRead('c1', 'me', '2026-10-11T10:00:10Z', 'me')
    expect(unread()).toBe(1)

    store().applyRead('c1', 'me', '2026-10-11T10:01:00Z', 'me')
    expect(unread()).toBe(0)
  })
})
