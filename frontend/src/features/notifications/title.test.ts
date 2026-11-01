import { describe, expect, it } from 'vitest'
import type { Conversation } from '../chat/types'
import { APP_TITLE, documentTitle, totalUnread } from './title'

function conversation(id: string, unreadCount: number, isMuted = false): Conversation {
  return {
    id,
    type: 'Group',
    name: id,
    avatarUrl: null,
    updatedAt: '2026-10-11T09:00:00Z',
    lastMessage: null,
    unreadCount,
    otherUserId: null,
    isMuted,
    leftAt: null,
  }
}

const byId = (...list: Conversation[]) => Object.fromEntries(list.map((c) => [c.id, c]))

describe('totalUnread', () => {
  it('adds up the unread messages of every conversation', () => {
    expect(totalUnread(byId(conversation('a', 2), conversation('b', 3)))).toBe(5)
  })

  it('leaves out the conversations that are muted', () => {
    expect(totalUnread(byId(conversation('a', 2), conversation('b', 30, true)))).toBe(2)
  })

  it('is zero when nothing is unread, or nothing is loaded', () => {
    expect(totalUnread(byId(conversation('a', 0)))).toBe(0)
    expect(totalUnread({})).toBe(0)
  })
})

describe('documentTitle', () => {
  it('is the plain name when nothing is unread', () => {
    expect(documentTitle(0)).toBe(APP_TITLE)
  })

  it('puts the number in front of the name', () => {
    expect(documentTitle(3)).toBe('(3) ChatNET')
  })

  it('reads 99+ from the cap up, as the sidebar does', () => {
    expect(documentTitle(99)).toBe('(99) ChatNET')
    expect(documentTitle(100)).toBe('(99+) ChatNET')
    expect(documentTitle(450)).toBe('(99+) ChatNET')
  })
})
