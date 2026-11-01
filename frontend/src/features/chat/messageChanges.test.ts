import { describe, expect, it } from 'vitest'
import {
  EDIT_WINDOW_MS,
  afterDeletion,
  canDelete,
  canEdit,
  isNewer,
  isPreviewOf,
  latest,
  withChangedPreview,
} from './messageChanges'
import type { Conversation, Message } from './types'

function message(overrides: Partial<Message> = {}): Message {
  return {
    id: 'm1',
    conversationId: 'c1',
    senderId: 'bob',
    senderDisplayName: 'Bob',
    content: 'hello',
    createdAt: '2026-10-11T10:00:00Z',
    clientMessageId: null,
    editedAt: null,
    editCount: 0,
    isDeleted: false,
    attachments: [],
    ...overrides,
  }
}

function conversation(overrides: Partial<Conversation> = {}): Conversation {
  return {
    id: 'c1',
    type: 'Direct',
    name: 'Bob',
    avatarUrl: null,
    updatedAt: '2026-10-11T10:00:00Z',
    lastMessage: null,
    unreadCount: 0,
    otherUserId: null,
    isMuted: false,
    leftAt: null,
    ...overrides,
  }
}

const sentAt = new Date('2026-10-11T10:00:00Z').getTime()

// ##### Which copy is the newer #####

describe('isNewer', () => {
  it('ranks a message by how often it was edited', () => {
    expect(isNewer(message({ editCount: 2 }), message({ editCount: 1 }))).toBe(true)
    expect(isNewer(message({ editCount: 1 }), message({ editCount: 2 }))).toBe(false)
  })

  it('does not call the same version newer', () => {
    expect(isNewer(message({ editCount: 1 }), message({ editCount: 1 }))).toBe(false)
    expect(isNewer(message({ isDeleted: true }), message({ isDeleted: true }))).toBe(false)
  })

  it('ranks a deleted message above any number of edits', () => {
    expect(isNewer(message({ isDeleted: true, editCount: 0 }), message({ editCount: 9 }))).toBe(true)
    expect(isNewer(message({ editCount: 9 }), message({ isDeleted: true }))).toBe(false)
  })
})

describe('latest', () => {
  it('keeps the held copy when it is the later version', () => {
    const held = message({ editCount: 2, content: 'held' })

    expect(latest(held, message({ editCount: 1, content: 'late' }))).toBe(held)
  })

  it('takes the incoming copy when it is later, or the same version (it is fresher)', () => {
    const incoming = message({ editCount: 1, content: 'new' })

    expect(latest(message({ editCount: 0 }), incoming)).toBe(incoming)
    expect(latest(message({ editCount: 1 }), incoming)).toBe(incoming)
  })

  it('never takes a copy back from deleted', () => {
    const held = message({ isDeleted: true, content: null })

    expect(latest(held, message({ editCount: 5 }))).toBe(held)
  })

  it('takes the incoming copy when nothing is held', () => {
    const incoming = message()

    expect(latest(undefined, incoming)).toBe(incoming)
  })
})

// ##### What the reader may do #####

describe('canEdit', () => {
  it('is for the reader’s own message inside the window', () => {
    expect(canEdit(message({ senderId: 'me' }), 'me', sentAt + EDIT_WINDOW_MS - 1)).toBe(true)
  })

  it('stops at the end of the window', () => {
    expect(canEdit(message({ senderId: 'me' }), 'me', sentAt + EDIT_WINDOW_MS)).toBe(false)
  })

  it('is never for someone else’s message, an admin included', () => {
    expect(canEdit(message({ senderId: 'bob' }), 'me', sentAt + 1000)).toBe(false)
  })

  it('is never for a deleted message', () => {
    expect(canEdit(message({ senderId: 'me', isDeleted: true }), 'me', sentAt + 1000)).toBe(false)
  })
})

describe('canDelete', () => {
  it('is for the reader’s own message whenever, whatever their role', () => {
    expect(canDelete(message({ senderId: 'me' }), 'me', undefined)).toBe(true)
    expect(canDelete(message({ senderId: 'me' }), 'me', 'Member')).toBe(true)
  })

  it('is for anyone’s message when the reader owns or administers the group', () => {
    expect(canDelete(message({ senderId: 'bob' }), 'me', 'Owner')).toBe(true)
    expect(canDelete(message({ senderId: 'bob' }), 'me', 'Admin')).toBe(true)
  })

  it('is not for someone else’s message otherwise, or when the role is not known', () => {
    expect(canDelete(message({ senderId: 'bob' }), 'me', 'Member')).toBe(false)
    expect(canDelete(message({ senderId: 'bob' }), 'me', undefined)).toBe(false)
  })

  it('is not for a message that is already deleted', () => {
    expect(canDelete(message({ senderId: 'me', isDeleted: true }), 'me', 'Owner')).toBe(false)
  })
})

// ##### The sidebar #####

const preview = (overrides = {}) => ({
  senderId: 'bob',
  content: 'hello',
  createdAt: '2026-10-11T10:00:00Z',
  attachmentCount: 0,
  ...overrides,
})

describe('isPreviewOf', () => {
  it('recognises the preview of a message by sender and time', () => {
    expect(isPreviewOf(preview(), message())).toBe(true)
    expect(isPreviewOf(preview({ senderId: 'me' }), message())).toBe(false)
    expect(isPreviewOf(preview({ createdAt: '2026-10-11T10:00:01Z' }), message())).toBe(false)
    expect(isPreviewOf(null, message())).toBe(false)
  })
})

describe('withChangedPreview', () => {
  it('shows the new text when the preview is of the message', () => {
    const shown = conversation({ lastMessage: preview() })

    const next = withChangedPreview(shown, message({ content: 'edited', editCount: 1 }))

    expect(next.lastMessage?.content).toBe('edited')
  })

  it('shows a deleted message with no text and no files', () => {
    const shown = conversation({ lastMessage: preview({ attachmentCount: 2 }) })

    const next = withChangedPreview(shown, message({ isDeleted: true, content: null }))

    expect(next.lastMessage).toMatchObject({ content: null, attachmentCount: 0 })
  })

  it('counts the files of an edited message', () => {
    const shown = conversation({ lastMessage: preview() })
    const files = [{ id: 'a1', type: 'File' as const, fileName: 'a', contentType: 'text/plain', sizeBytes: 1, url: 'u' }]

    expect(withChangedPreview(shown, message({ attachments: files })).lastMessage?.attachmentCount).toBe(1)
  })

  it('leaves the same object when the preview already says what the message says', () => {
    const shown = conversation({ lastMessage: preview({ content: 'edited' }) })

    expect(withChangedPreview(shown, message({ content: 'edited', editCount: 1 }))).toBe(shown)
  })

  it('leaves the same object when the preview is of another message', () => {
    const shown = conversation({ lastMessage: preview({ createdAt: '2026-10-11T11:00:00Z' }) })

    expect(withChangedPreview(shown, message({ content: 'edited' }))).toBe(shown)
  })
})

// ##### Unread after a deletion #####

describe('afterDeletion', () => {
  const held = [
    message({ id: 'm1', createdAt: '2026-10-11T10:00:00Z' }),
    message({ id: 'm2', createdAt: '2026-10-11T10:01:00Z' }),
    message({ id: 'm3', createdAt: '2026-10-11T10:02:00Z' }),
    message({ id: 'm4', senderId: 'me', createdAt: '2026-10-11T10:03:00Z' }),
  ]

  it('takes one off when the deleted message is among the unread ones (the newest from others)', () => {
    const shown = conversation({ unreadCount: 2 })

    expect(afterDeletion(shown, held, held[2], 'me').unreadCount).toBe(1)
    expect(afterDeletion(shown, held, held[1], 'me').unreadCount).toBe(1)
  })

  it('leaves the count when the deleted message was already read', () => {
    const shown = conversation({ unreadCount: 2 })

    expect(afterDeletion(shown, held, held[0], 'me')).toBe(shown)
  })

  it('does not count messages that were deleted before when ranking the unread ones', () => {
    const before = [message({ id: 'm0', isDeleted: true, content: null }), ...held]
    const shown = conversation({ unreadCount: 3 })

    // m1, m2, m3 are the three unread; the deleted m0 is not one of them.
    expect(afterDeletion(shown, before, held[0], 'me').unreadCount).toBe(2)
  })

  it('leaves the count when the reader deleted their own message, or nothing is unread', () => {
    expect(afterDeletion(conversation({ unreadCount: 2 }), held, held[3], 'me').unreadCount).toBe(2)
    expect(afterDeletion(conversation({ unreadCount: 0 }), held, held[2], 'me').unreadCount).toBe(0)
  })

  it('never goes below zero and keeps the same object when nothing changes', () => {
    const shown = conversation({ unreadCount: 1 })

    expect(afterDeletion(shown, held, held[2], 'me').unreadCount).toBe(0)
    expect(afterDeletion(shown, held, held[0], 'me')).toBe(shown)
  })


  it('does not count a deletion of the reader’s own message either when the messages are not held', () => {
    const shown = conversation({ unreadCount: 2, lastMessage: preview({ senderId: 'me', createdAt: '2026-10-11T10:02:00Z' }) })
    const own = message({ senderId: 'me', createdAt: '2026-10-11T10:02:00Z' })

    expect(afterDeletion(shown, undefined, own, 'me')).toBe(shown)
  })

  it('ranks the unread ones among the messages that are still there: a newer deleted one is not unread', () => {
    const before = [
      message({ id: 'm1', createdAt: '2026-10-11T10:00:00Z' }),
      message({ id: 'm2', createdAt: '2026-10-11T10:01:00Z' }),
      message({ id: 'm3', createdAt: '2026-10-11T10:02:00Z' }),
      message({ id: 'm5', createdAt: '2026-10-11T10:04:00Z', isDeleted: true, content: null }),
    ]
    const shown = conversation({ unreadCount: 2 })

    // The unread ones are m2 and m3: the deleted m5 was not counted.
    expect(afterDeletion(shown, before, before[1], 'me').unreadCount).toBe(1)
  })

  it('without the messages, can only tell the newest message of the conversation', () => {
    const shown = conversation({ unreadCount: 2, lastMessage: preview({ createdAt: '2026-10-11T10:02:00Z' }) })

    expect(afterDeletion(shown, undefined, message({ createdAt: '2026-10-11T10:02:00Z' }), 'me').unreadCount).toBe(1)
    expect(afterDeletion(shown, undefined, message({ createdAt: '2026-10-11T10:01:00Z' }), 'me')).toBe(shown)
  })
})
