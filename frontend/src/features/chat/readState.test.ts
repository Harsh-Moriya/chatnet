import { describe, expect, it } from 'vitest'
import { messageToMarkRead, readersOf, seenFor, UNREAD_CAP, unreadLabel } from './readState'
import type { Member, Message } from './types'

function message(id: string, overrides: Partial<Message> = {}): Message {
  return {
    id,
    conversationId: 'c1',
    senderId: 'bob',
    senderDisplayName: 'Bob',
    content: `content of ${id}`,
    createdAt: '2026-10-11T10:00:00Z',
    clientMessageId: null,
    editedAt: null,
    editCount: 0,
    isDeleted: false,
    attachments: [],
    ...overrides,
  }
}

function member(userId: string, lastReadAt: string | null, displayName = userId): Member {
  return { userId, userName: userId, displayName, avatarUrl: null, role: 'Member', lastReadAt }
}

// ##### The badge text #####

describe('the unread label', () => {
  it('is empty when nothing is unread', () => {
    expect(unreadLabel(0)).toBe('')
  })

  it('is the number for small counts', () => {
    expect(unreadLabel(1)).toBe('1')
    expect(unreadLabel(UNREAD_CAP - 1)).toBe(String(UNREAD_CAP - 1))
  })

  it('stops at the cap, because the server does not count past it', () => {
    expect(unreadLabel(UNREAD_CAP)).toBe(`${UNREAD_CAP - 1}+`)
    expect(unreadLabel(UNREAD_CAP + 40)).toBe(`${UNREAD_CAP - 1}+`)
  })
})

// ##### When to tell the server a conversation was read #####

describe('which message to mark as read', () => {
  const messages = [message('a'), message('b'), message('c')]
  const ready = { visible: true, atBottom: true, unreadCount: 2, messages }

  it('is the newest message when the reader is looking at the end of a conversation with unread messages', () => {
    expect(messageToMarkRead(ready)).toBe('c')
  })

  it('is nothing while the page is hidden, because nobody can see the messages', () => {
    expect(messageToMarkRead({ ...ready, visible: false })).toBeNull()
  })

  it('is nothing while the reader is scrolled up, because the newest messages are off screen', () => {
    expect(messageToMarkRead({ ...ready, atBottom: false })).toBeNull()
  })

  it('is nothing when nothing is unread, so reading does not repeat itself', () => {
    expect(messageToMarkRead({ ...ready, unreadCount: 0 })).toBeNull()
  })

  it('is nothing before any message is loaded', () => {
    expect(messageToMarkRead({ ...ready, messages: [] })).toBeNull()
    expect(messageToMarkRead({ ...ready, messages: undefined })).toBeNull()
  })
})

// ##### Who has seen a message #####

describe('who has read a message', () => {
  const sent = message('m1', { senderId: 'me', createdAt: '2026-10-11T10:00:10Z' })

  it('is every other member whose read position is at or after the message', () => {
    const members = [
      member('me', '2026-10-11T10:00:10Z'),
      member('ann', '2026-10-11T10:00:10Z'),
      member('bob', '2026-10-11T10:05:00Z'),
      member('cat', '2026-10-11T10:00:09Z'),
      member('dan', null),
    ]

    expect(readersOf(sent, members, 'me').map((m) => m.userId)).toEqual(['ann', 'bob'])
  })

  it('compares times by value, not as text', () => {
    const members = [member('ann', '2026-10-11T10:00:10.5Z')]

    expect(readersOf(sent, members, 'me')).toHaveLength(1)
  })

  it('never counts the reader themself', () => {
    expect(readersOf(sent, [member('me', '2026-10-11T11:00:00Z')], 'me')).toEqual([])
  })

  it('is nobody when the members are not known yet', () => {
    expect(readersOf(sent, undefined, 'me')).toEqual([])
  })
})

describe('the "seen" mark under your newest message', () => {
  const mine = message('m1', { senderId: 'me', createdAt: '2026-10-11T10:00:10Z' })
  const theirs = message('m2', { senderId: 'ann', createdAt: '2026-10-11T10:00:20Z' })

  it('says "Seen" in a direct chat once the other person has read it', () => {
    const seen = seenFor([mine], [member('me', null), member('ann', '2026-10-11T10:00:10Z', 'Ann')], 'me', false)

    expect(seen).toEqual({ messageId: 'm1', label: 'Seen', title: 'Seen by Ann' })
  })

  it('counts the readers in a group and names them in the tooltip', () => {
    const members = [
      member('me', null),
      member('ann', '2026-10-11T10:00:10Z', 'Ann'),
      member('bob', '2026-10-11T10:01:00Z', 'Bob'),
      member('cat', null, 'Cat'),
    ]

    expect(seenFor([mine], members, 'me', true)).toEqual({
      messageId: 'm1',
      label: 'Seen by 2',
      title: 'Seen by Ann, Bob',
    })
  })

  it('belongs to the newest message of yours, not to an older one', () => {
    const newer = message('m3', { senderId: 'me', createdAt: '2026-10-11T10:00:30Z' })
    const members = [member('ann', '2026-10-11T10:00:25Z', 'Ann')]

    // Ann has seen the older message but not the newer one, so nothing is shown at all: a mark under
    // the older message would be easy to read as "seen" for the one the reader just sent.
    expect(seenFor([mine, theirs, newer], members, 'me', false)).toBeNull()
  })

  it('skips messages that other people sent when looking for yours', () => {
    const members = [member('ann', '2026-10-11T10:05:00Z', 'Ann')]

    expect(seenFor([mine, theirs], members, 'me', false)?.messageId).toBe('m1')
  })

  it('is nothing when you have sent nothing, or nobody has read it', () => {
    expect(seenFor([theirs], [member('ann', '2026-10-11T10:05:00Z')], 'me', false)).toBeNull()
    expect(seenFor([mine], [member('ann', null)], 'me', false)).toBeNull()
    expect(seenFor([], [], 'me', false)).toBeNull()
  })
})
