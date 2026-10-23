import { describe, expect, it } from 'vitest'
import type { Conversation, Message } from '../chat/types'
import {
  conversationTag,
  messageAlertText,
  notificationAlertText,
  shouldAlertForMessage,
  shouldAlertForNotification,
  type Viewing,
} from './alerts'
import type { NotificationItem } from './types'

const away: Viewing = { looking: false, openConversationId: null }
const looking = (openConversationId: string | null): Viewing => ({ looking: true, openConversationId })

function message(overrides: Partial<Message> = {}): Message {
  return {
    id: 'm1',
    conversationId: 'c1',
    senderId: 'bob',
    senderDisplayName: 'Bob',
    content: 'hello',
    createdAt: '2026-10-11T10:00:00Z',
    clientMessageId: null,
    attachments: [],
    ...overrides,
  }
}

function conversation(overrides: Partial<Conversation> = {}): Conversation {
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
    ...overrides,
  }
}

function notification(overrides: Partial<NotificationItem> = {}): NotificationItem {
  return {
    id: 'n1',
    type: 'Mention',
    conversationId: 'c1',
    actorId: 'bob',
    actorDisplayName: 'Bob',
    messageId: 'm1',
    messagePreview: '@me are you there',
    isRead: false,
    createdAt: '2026-10-11T10:00:00Z',
    ...overrides,
  }
}

describe('when a new message raises an alert', () => {
  it('does when the tab is in the background', () => {
    expect(shouldAlertForMessage(message(), 'me', conversation(), away)).toBe(true)
  })

  it('does when another conversation is open', () => {
    expect(shouldAlertForMessage(message(), 'me', conversation(), looking('c2'))).toBe(true)
  })

  it('does when no conversation is open', () => {
    expect(shouldAlertForMessage(message(), 'me', conversation(), looking(null))).toBe(true)
  })

  it('does not when the reader is looking at that conversation', () => {
    expect(shouldAlertForMessage(message(), 'me', conversation(), looking('c1'))).toBe(false)
  })

  it('does when that conversation is open but the reader is not looking at it', () => {
    expect(shouldAlertForMessage(message(), 'me', conversation(), { looking: false, openConversationId: 'c1' })).toBe(true)
  })

  it('does not for the reader\x27s own message from another tab', () => {
    expect(shouldAlertForMessage(message({ senderId: 'me' }), 'me', conversation(), away)).toBe(false)
  })

  it('does not when the conversation is muted', () => {
    expect(shouldAlertForMessage(message(), 'me', conversation({ isMuted: true }), away)).toBe(false)
  })

  it('does when the conversation is not known yet', () => {
    expect(shouldAlertForMessage(message(), 'me', undefined, away)).toBe(true)
  })

  it('does when nobody is signed in to compare with', () => {
    expect(shouldAlertForMessage(message({ senderId: 'bob' }), null, conversation(), away)).toBe(true)
  })
})

describe('when a notification raises an alert', () => {
  it('does for a mention in the background', () => {
    expect(shouldAlertForNotification(notification(), away)).toBe(true)
  })

  it('does for a mention in another conversation', () => {
    expect(shouldAlertForNotification(notification(), looking('c2'))).toBe(true)
  })

  it('does not for a mention in the conversation being read', () => {
    expect(shouldAlertForNotification(notification(), looking('c1'))).toBe(false)
  })

  it('does for being added to a group while away', () => {
    expect(shouldAlertForNotification(notification({ type: 'MemberAdded', messageId: null }), away)).toBe(true)
  })

  it('does not for being added to a group while looking at the page', () => {
    expect(shouldAlertForNotification(notification({ type: 'MemberAdded', messageId: null }), looking('c2'))).toBe(false)
  })
})

describe('what an alert says', () => {
  it('names the sender in a direct conversation', () => {
    const text = messageAlertText(message(), conversation({ type: 'Direct', name: 'Bob' }))

    expect(text.title).toBe('Bob')
    expect(text.body).toBe('hello')
  })

  it('names the sender and the group in a group', () => {
    expect(messageAlertText(message(), conversation()).title).toBe('Bob in Team')
  })

  it('shortens a long message', () => {
    const text = messageAlertText(message({ content: 'x'.repeat(300) }), conversation())

    expect(text.body).toHaveLength(101)
    expect(text.body.endsWith('…')).toBe(true)
  })

  it('describes a message of only files', () => {
    const files = (count: number) =>
      messageAlertText(
        message({
          content: '',
          attachments: Array.from({ length: count }, (_, i) => ({
            id: `a${i}`, type: 'File' as const, fileName: 'f', contentType: 'text/plain', sizeBytes: 1, url: 'u',
          })),
        }),
        conversation(),
      ).body

    expect(files(1)).toBe('Sent a file')
    expect(files(3)).toBe('Sent 3 files')
  })

  it('has an empty body for a deleted message', () => {
    expect(messageAlertText(message({ content: null }), conversation()).body).toBe('')
  })

  it('gives a message and a mention in the same conversation the same tag, so one replaces the other', () => {
    expect(messageAlertText(message(), conversation()).tag).toBe(conversationTag('c1'))
    expect(notificationAlertText(notification(), 'Team').tag).toBe(conversationTag('c1'))
  })

  it('gives different conversations different tags', () => {
    expect(conversationTag('c1')).not.toBe(conversationTag('c2'))
  })

  it('says who mentioned the reader and where, with the start of the message', () => {
    const text = notificationAlertText(notification(), 'Team')

    expect(text.title).toBe('Bob mentioned you in Team')
    expect(text.body).toBe('@me are you there')
  })

  it('says who added the reader to which group', () => {
    const text = notificationAlertText(notification({ type: 'MemberAdded', messagePreview: null, messageId: null }), 'Team')

    expect(text.title).toBe('Bob added you to Team')
    expect(text.body).toBe('')
  })

  it('still says something when the conversation is not in the sidebar', () => {
    expect(notificationAlertText(notification(), undefined).title).toBe('Bob mentioned you in a conversation')
    expect(notificationAlertText(notification({ type: 'MemberAdded' }), undefined).title).toBe('Bob added you to a group')
  })

  it('gives a notification with no conversation a tag of its own', () => {
    expect(notificationAlertText(notification({ conversationId: null }), undefined).tag).toBe('notification-n1')
  })
})
