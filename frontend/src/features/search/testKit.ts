import type { Message, MessageWire } from '../chat/types'
import { wordsOf } from './query/normalize'
import type { Listed } from './sync/plan'
import type { ChangesWire } from './sync/engine'
import type { StoredDeleted, StoredSealed, StoredText } from './store/types'

// What the tests of the local store, the sync and the search share: builders for what is kept, and a stand-in for the server.

export const ANA = '3f2a9c1e-7b4d-4e8a-9c55-0a1b2c3d4e5f'
export const BEN = '9d8c7b6a-5e4f-4a3b-8c2d-1e0f9a8b7c6d'

let counter = 0

// What every message the store keeps has, whatever state it is in.
function baseOf(createdAt = '2026-11-12T10:00:00Z') {
  return {
    userId: ANA,
    conversationId: 'c1',
    id: `m${String(++counter).padStart(6, '0')}`,
    senderId: BEN,
    senderName: 'Ben',
    createdAt,
    at: Date.parse(createdAt),
    editCount: 0,
    changeSeq: 1,
  }
}

// A message as the store keeps it. The words follow the text unless they are given.
export function stored(overrides: Partial<StoredText> = {}): StoredText {
  const text = overrides.text ?? 'hello world'

  return { ...baseOf(overrides.createdAt), state: 'text', text, files: [], ...overrides, words: overrides.words ?? wordsOf(text) }
}

export function sealedStored(overrides: Partial<StoredSealed> = {}): StoredSealed {
  return {
    ...baseOf(overrides.createdAt),
    state: 'sealed',
    sealed: { ciphertext: 'AQEAAAAB', keyEpoch: 1, clientMessageId: 'cid', files: [] },
    ...overrides,
  }
}

export function deletedStored(overrides: Partial<StoredDeleted> = {}): StoredDeleted {
  return { ...baseOf(overrides.createdAt), state: 'deleted', ...overrides }
}

// The server's account of a message. The test stand-in for opening reads the text from `ciphertext`, so what is written there is what
// the message says.
export function wireOf(overrides: Partial<MessageWire> = {}): MessageWire {
  return {
    id: `w${String(++counter).padStart(6, '0')}`,
    conversationId: 'c1',
    senderId: BEN,
    senderDisplayName: 'Ben',
    ciphertext: 'hello world',
    keyEpoch: 1,
    createdAt: '2026-11-12T10:00:00Z',
    attachments: [],
    clientMessageId: `cid-${counter}`,
    editedAt: null,
    editCount: 0,
    isDeleted: false,
    changeSeq: 1,
    ...overrides,
  }
}

// What opening does in these tests: the text is the "sealed" blob itself, and a blob that starts with "locked:" cannot be opened.
export async function open(wires: MessageWire[]): Promise<Message[]> {
  return wires.map((wire): Message => {
    const locked = wire.ciphertext?.startsWith('locked:') ?? false

    return {
      ...wire,
      content: wire.ciphertext === null || locked ? null : wire.ciphertext,
      unreadable: locked ? 'noKey' : null,
      attachments: wire.attachments.map((attachment) => ({ ...attachment, file: null })),
    }
  })
}

const HISTORY_PAGE = 50

// A server that holds some conversations and answers the two requests the store reads with, as the real one does: the history in pages
// of 50 (oldest first, the 50 just older than a message), and the feed from a point, in pages of a size the test chooses. It counts
// the requests it was asked, so a test can say what was and was not asked for.
export class FakeServer {
  private readonly conversations = new Map<string, { messages: MessageWire[]; seq: number }>()
  readonly historyCalls: { conversationId: string; before: string | null }[] = []
  readonly changesCalls: { conversationId: string; since: number }[] = []

  feedPage = 100
  // Failures to make: the n-th next request of a kind fails with this.
  failHistoryAt: { call: number; error: Error } | null = null
  failChanges: Error | null = null

  private of(conversationId: string) {
    let found = this.conversations.get(conversationId)
    if (!found) {
      found = { messages: [], seq: 0 }
      this.conversations.set(conversationId, found)
    }
    return found
  }

  seq(conversationId: string): number {
    return this.of(conversationId).seq
  }

  // A message sent now, one second after the one before it.
  say(conversationId: string, text: string, extra: Partial<MessageWire> = {}): MessageWire {
    const conversation = this.of(conversationId)
    const at = new Date(Date.parse('2026-11-12T10:00:00Z') + conversation.messages.length * 1000).toISOString()
    const wire = wireOf({ conversationId, ciphertext: text, createdAt: at, changeSeq: ++conversation.seq, ...extra })
    conversation.messages.push(wire)
    return wire
  }

  edit(conversationId: string, id: string, text: string): MessageWire {
    const conversation = this.of(conversationId)
    const at = conversation.messages.findIndex((message) => message.id === id)
    const held = conversation.messages[at]
    conversation.messages[at] = { ...held, ciphertext: text, editCount: held.editCount + 1, changeSeq: ++conversation.seq }
    return conversation.messages[at]
  }

  remove(conversationId: string, id: string): MessageWire {
    const conversation = this.of(conversationId)
    const at = conversation.messages.findIndex((message) => message.id === id)
    conversation.messages[at] = { ...conversation.messages[at], ciphertext: null, isDeleted: true, changeSeq: ++conversation.seq }
    return conversation.messages[at]
  }

  listed(...ids: string[]): Listed[] {
    return ids.map((id) => ({ id, changeSeq: this.of(id).seq }))
  }

  history = async (conversationId: string, before: string | null): Promise<MessageWire[]> => {
    this.historyCalls.push({ conversationId, before })
    const failure = this.failHistoryAt
    if (failure && this.historyCalls.length === failure.call) {
      this.failHistoryAt = null
      throw failure.error
    }

    const { messages } = this.of(conversationId)
    const end = before === null ? messages.length : messages.findIndex((message) => message.id === before)
    return messages.slice(Math.max(0, end - HISTORY_PAGE), end)
  }

  changes = async (conversationId: string, since: number): Promise<ChangesWire> => {
    this.changesCalls.push({ conversationId, since })
    if (this.failChanges) throw this.failChanges

    const conversation = this.of(conversationId)
    if (since > conversation.seq) throw new CursorAheadError()

    const newer = conversation.messages.filter((message) => message.changeSeq > since).sort((a, b) => a.changeSeq - b.changeSeq)
    const page = newer.slice(0, this.feedPage)
    const more = newer.length > this.feedPage

    return { changes: page, cursor: more ? page[page.length - 1].changeSeq : conversation.seq, more }
  }
}

export class CursorAheadError extends Error {
  constructor() {
    super('The point asked from is past the newest change.')
  }
}
