import { HISTORY_PAGE_SIZE } from '../../chat/history'
import type { Message, MessageWire } from '../../chat/types'
import { storedFrom } from '../ingest'
import type { LocalStore, StoredMessage } from '../store/types'
import { planSync, type Listed, type Work } from './plan'

// Keeps the local store in step with the server: the history of a conversation read once, newest first, and from then on what changed
// since, by the changes feed. Everything the world does here is a dependency, so the rules can be tested against a fake server.

// One page of the feed as the server sends it.
export interface ChangesWire {
  changes: MessageWire[]
  cursor: number
  more: boolean
}

export interface SyncDeps {
  store: LocalStore
  userId: string
  // A page of the history: the 50 messages just older than `before` (the latest 50 when it is null), oldest first.
  history: (conversationId: string, before: string | null, signal?: AbortSignal) => Promise<MessageWire[]>
  changes: (conversationId: string, since: number, signal?: AbortSignal) => Promise<ChangesWire>
  // Opens what the server sent. It writes nothing: the engine writes what it opens, with the read point.
  open: (wires: MessageWire[]) => Promise<Message[]>
  // Whether a failed feed request was the server saying the point asked from is past the newest change it has.
  cursorAhead: (error: unknown) => boolean
  onHistoryPending?: (count: number) => void
  signal?: AbortSignal
}

// How many conversations are read at once: enough to be quick, few enough not to crowd out what the person is doing.
const CONCURRENT_CONVERSATIONS = 2

const kept = (userId: string, messages: Message[]): StoredMessage[] =>
  messages.flatMap((message) => {
    const stored = storedFrom(userId, message)
    return stored ? [stored] : []
  })

// The history, from where it was left, to its start. The conversation's number as it was when this began is kept with the first page,
// so the feed that follows covers whatever changed while the history was being read, and an interruption starts again from the same
// number.
async function readHistory(deps: SyncDeps, conversationId: string, startSeq: number, from: string | null): Promise<void> {
  let before = from

  for (;;) {
    deps.signal?.throwIfAborted()

    const wires = await deps.history(conversationId, before, deps.signal)
    const opened = await deps.open(wires)

    // A page shorter than the server's is the start of the history.
    const done = wires.length < HISTORY_PAGE_SIZE
    if (wires.length > 0) before = wires[0].id

    await deps.store.apply(deps.userId, conversationId, kept(deps.userId, opened), {
      startSeq,
      oldestId: before,
      historyRead: done,
      cursor: done ? startSeq : null,
    })

    if (done) return
  }
}

// Everything that changed since the cursor, a page at a time. 'ahead' when the server says the cursor is past its newest change.
async function readChanges(deps: SyncDeps, conversationId: string): Promise<'done' | 'ahead'> {
  let cursor = (await deps.store.readPoint(deps.userId, conversationId))?.cursor ?? 0

  for (;;) {
    deps.signal?.throwIfAborted()

    let page: ChangesWire
    try {
      page = await deps.changes(conversationId, cursor, deps.signal)
    } catch (error) {
      if (deps.cursorAhead(error)) return 'ahead'
      throw error
    }

    // A feed that says there is more without moving would be asked the same question for ever.
    if (page.more && page.cursor <= cursor) throw new Error('The changes feed did not move on.')

    const opened = await deps.open(page.changes)
    await deps.store.apply(deps.userId, conversationId, kept(deps.userId, opened), { cursor: page.cursor })

    cursor = page.cursor
    if (!page.more) return 'done'
  }
}

async function syncConversation(deps: SyncDeps, work: Work, rebuilt = false): Promise<void> {
  const point = await deps.store.readPoint(deps.userId, work.id)

  if (!point?.historyRead) await readHistory(deps, work.id, point?.startSeq ?? work.changeSeq, point?.oldestId ?? null)

  if ((await readChanges(deps, work.id)) === 'done') return

  // What this device holds for the conversation cannot be trusted: the server has gone back to an older state, and the numbers it will
  // give from here on are not the ones this device has seen. It is read again from the start, once; a server that says it again
  // straight after is left for the next run.
  if (rebuilt) throw new Error('The changes feed still asks for a point past its newest change after the conversation was read again.')

  await deps.store.forgetConversation(deps.userId, work.id)
  await syncConversation(deps, work, true)
}

export interface SyncResult {
  // The conversations that could not be read this time. They are asked for again on the next run.
  failed: string[]
}

// Reads what each conversation in the list needs. One conversation that fails does not stop the others.
export async function syncAll(deps: SyncDeps, listed: readonly Listed[]): Promise<SyncResult> {
  await deps.store.prepare(deps.userId)

  const work = planSync(listed, await deps.store.readPoints(deps.userId))
  let pending = work.filter((item) => item.history).length
  deps.onHistoryPending?.(pending)

  const failed: string[] = []
  const queue = [...work]

  async function worker(): Promise<void> {
    for (let item = queue.shift(); item; item = queue.shift()) {
      deps.signal?.throwIfAborted()

      try {
        await syncConversation(deps, item)
        if (item.history) deps.onHistoryPending?.(--pending)
      } catch (error) {
        if (deps.signal?.aborted) throw error
        failed.push(item.id)
      }
    }
  }

  await Promise.all(Array.from({ length: Math.min(CONCURRENT_CONVERSATIONS, work.length) }, worker))
  return { failed }
}
