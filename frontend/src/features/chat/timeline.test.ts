import { describe, expect, it } from 'vitest'
import { buildTimeline, RUN_GAP_MS, type TimelineItem } from './timeline'
import type { Message, PendingMessage } from './types'

// Built in local time so the day boundaries are the same in any time zone. "Now" is Sunday
// 11 October 2026, 15:00.
const NOW = new Date(2026, 9, 11, 15, 0)
const at = (day: number, hour: number, minute = 0, second = 0) =>
  new Date(2026, 9, day, hour, minute, second).toISOString()

function message(id: string, senderId: string, createdAt: string, overrides: Partial<Message> = {}): Message {
  return {
    id,
    conversationId: 'c1',
    senderId,
    senderDisplayName: senderId.toUpperCase(),
    content: `text ${id}`,
    createdAt,
    clientMessageId: null,
    ...overrides,
  }
}

function pending(clientMessageId: string, createdAt: string): PendingMessage {
  return { clientMessageId, conversationId: 'c1', content: 'draft', createdAt, status: 'sending', error: null }
}

function build(
  messages: Message[],
  pendingMessages: PendingMessage[] = [],
  options: { isGroup?: boolean } = {},
) {
  return buildTimeline(messages, pendingMessages, {
    isGroup: options.isGroup ?? false,
    currentUserId: 'me',
    now: NOW,
    locale: 'en-US',
  })
}

const kinds = (items: TimelineItem[]) => items.map((i) => i.kind)
const messages = (items: TimelineItem[]) =>
  items.filter((i): i is Extract<TimelineItem, { kind: 'message' }> => i.kind === 'message')

describe('day dividers', () => {
  it('has nothing to show for an empty conversation', () => {
    expect(build([])).toEqual([])
  })

  it('starts with a divider for the day of the first message', () => {
    const items = build([message('a', 'bob', at(11, 9))])

    expect(items[0]).toMatchObject({ kind: 'day', label: 'Today' })
    expect(kinds(items)).toEqual(['day', 'message'])
  })

  it('adds a divider each time the day changes', () => {
    const items = build([
      message('a', 'bob', at(9, 9)),
      message('b', 'bob', at(10, 9)),
      message('c', 'bob', at(10, 10)),
      message('d', 'bob', at(11, 9)),
    ])

    expect(items.filter((i) => i.kind === 'day').map((i) => (i as { label: string }).label)).toEqual([
      'Friday',
      'Yesterday',
      'Today',
    ])
  })

  it('does not repeat a divider within a day', () => {
    const items = build([message('a', 'bob', at(11, 9)), message('b', 'bob', at(11, 14))])

    expect(items.filter((i) => i.kind === 'day')).toHaveLength(1)
  })

  it('gives each divider a key that stays the same, so the list does not remount it', () => {
    const first = build([message('a', 'bob', at(11, 9))])
    const second = build([message('a', 'bob', at(11, 9)), message('b', 'bob', at(11, 10))])

    expect(first[0].key).toBe(second[0].key)
  })
})

describe('runs of messages from one sender', () => {
  it('continues a run for the same sender within five minutes', () => {
    const items = messages(
      build([message('a', 'bob', at(11, 9, 0)), message('b', 'bob', at(11, 9, 4, 59))]),
    )

    expect(items.map((i) => i.continuing)).toEqual([false, true])
  })

  it('breaks the run after more than five minutes', () => {
    const start = new Date(2026, 9, 11, 9, 0, 0).getTime()
    const later = new Date(start + RUN_GAP_MS + 1000).toISOString()
    const items = messages(build([message('a', 'bob', at(11, 9, 0)), message('b', 'bob', later)]))

    expect(items.map((i) => i.continuing)).toEqual([false, false])
  })

  it('breaks the run when the sender changes', () => {
    const items = messages(
      build([message('a', 'bob', at(11, 9, 0)), message('b', 'carol', at(11, 9, 1)), message('c', 'bob', at(11, 9, 2))]),
    )

    expect(items.map((i) => i.continuing)).toEqual([false, false, false])
  })

  it('breaks the run at midnight even for a short gap', () => {
    const items = messages(
      build([
        message('a', 'bob', new Date(2026, 9, 10, 23, 59, 30).toISOString()),
        message('b', 'bob', new Date(2026, 9, 11, 0, 0, 30).toISOString()),
      ]),
    )

    expect(items.map((i) => i.continuing)).toEqual([false, false])
  })
})

describe('who is named above a message', () => {
  it('names the sender of the first message of a run in a group, for other people only', () => {
    const items = messages(
      build(
        [
          message('a', 'bob', at(11, 9, 0)),
          message('b', 'bob', at(11, 9, 1)),
          message('c', 'me', at(11, 9, 2)),
          message('d', 'carol', at(11, 9, 3)),
        ],
        [],
        { isGroup: true },
      ),
    )

    expect(items.map((i) => i.showSender)).toEqual([true, false, false, true])
  })

  it('never names anyone in a direct conversation', () => {
    const items = messages(build([message('a', 'bob', at(11, 9, 0))], [], { isGroup: false }))

    expect(items[0].showSender).toBe(false)
  })

  it('says which messages are the signed-in user\'s own', () => {
    const items = messages(build([message('a', 'bob', at(11, 9)), message('b', 'me', at(11, 10))]))

    expect(items.map((i) => i.own)).toEqual([false, true])
  })
})

describe('unconfirmed messages', () => {
  it('come after every confirmed message, as the user\'s own', () => {
    const items = build([message('a', 'bob', at(11, 9))], [pending('p1', at(11, 9, 30))])

    const last = items[items.length - 1]
    expect(last).toMatchObject({ kind: 'pending' })
  })

  it('continue the run of the user\'s own previous message, but not another person\'s', () => {
    const afterOwn = build([message('a', 'me', at(11, 9))], [pending('p1', at(11, 9, 1))])
    const afterOther = build([message('a', 'bob', at(11, 9))], [pending('p1', at(11, 9, 1))])

    expect(afterOwn[afterOwn.length - 1]).toMatchObject({ kind: 'pending', continuing: true })
    expect(afterOther[afterOther.length - 1]).toMatchObject({ kind: 'pending', continuing: false })
  })

  it('open a new day when the last confirmed message was on an earlier day', () => {
    const items = build([message('a', 'bob', at(10, 22))], [pending('p1', at(11, 9))])

    expect(kinds(items)).toEqual(['day', 'message', 'day', 'pending'])
  })

  it('appear alone with a divider when nothing is confirmed yet', () => {
    const items = build([], [pending('p1', at(11, 9)), pending('p2', at(11, 9, 1))])

    expect(kinds(items)).toEqual(['day', 'pending', 'pending'])
  })

  it('keep their order', () => {
    const items = build([], [pending('p1', at(11, 9)), pending('p2', at(11, 9, 1)), pending('p3', at(11, 9, 2))])

    expect(items.filter((i) => i.kind === 'pending').map((i) => i.key)).toEqual(['p1', 'p2', 'p3'])
  })

  // When the server confirms a message the entry is replaced; the same key means React updates the
  // existing element instead of removing it and adding a new one, so it does not flash.
  it('share a key with the confirmed copy of the same message', () => {
    const waiting = build([], [pending('p1', at(11, 9))])
    const confirmed = build([message('server-id', 'me', at(11, 9), { clientMessageId: 'p1' })])

    expect(waiting.find((i) => i.kind === 'pending')?.key).toBe('p1')
    expect(confirmed.find((i) => i.kind === 'message')?.key).toBe('p1')
  })

  it('leave messages without a client id keyed by their own id', () => {
    const items = build([message('server-id', 'bob', at(11, 9))])

    expect(items.find((i) => i.kind === 'message')?.key).toBe('server-id')
  })
})

describe('deleted messages', () => {
  it('stay in the timeline so the conversation has no gap', () => {
    const items = messages(build([message('a', 'bob', at(11, 9), { content: null })]))

    expect(items).toHaveLength(1)
    expect(items[0].message.content).toBeNull()
  })
})
