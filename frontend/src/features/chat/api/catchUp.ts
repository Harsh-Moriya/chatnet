import { HISTORY_PAGE_SIZE } from '../history'
import { useChatStore } from '../store/chatStore'
import { fetchMessages } from './chatApi'

// What to do after the live connection came back from a drop. Pushes sent while it was down are
// lost, so each conversation that has been loaded asks the server for everything after the newest
// message it holds, a page at a time, until a page comes back short. The server's history is the
// source of truth, and the store merges what it returns by id, so running this twice, or racing a
// push, is harmless. This is a reaction to the connection coming back, not to a message arriving.

// Reading further than this many pages (50 each) after a long absence costs more than starting the
// conversation over, which loads only the latest page and lets the reader scroll back for the rest.
export const CATCH_UP_MAX_PAGES = 10

// How many conversations are caught up at once. A reader with many loaded conversations should not
// open a request for every one of them in the same instant.
const CONCURRENT_CONVERSATIONS = 4

export interface CatchUpOptions {
  // Called when a conversation cannot be caught up from what is held: nothing to continue from, or
  // too far behind. Its messages have been dropped from the store; the caller makes it load again.
  onTooFarBehind: (conversationId: string) => void
}

interface Run {
  promise: Promise<void>
  // Set when catching up was requested again while this run was going. The connection dropped and
  // returned again, so messages may have been sent after this run's last request was answered.
  askedAgain: boolean
}

// At most one run per conversation. A second request joins the running one.
const running = new Map<string, Run>()

const chat = () => useChatStore.getState()

export function catchUp(conversationId: string, options: CatchUpOptions): Promise<void> {
  const existing = running.get(conversationId)
  if (existing) {
    existing.askedAgain = true
    return existing.promise
  }

  const run: Run = { promise: Promise.resolve(), askedAgain: false }
  running.set(conversationId, run)

  run.promise = (async () => {
    try {
      do {
        run.askedAgain = false
        await catchUpOnce(conversationId, options)
      } while (run.askedAgain)
    } finally {
      running.delete(conversationId)
    }
  })()

  return run.promise
}

// Every conversation the store has loaded. One that failed is left as it was and tried again from
// the same message after the next reconnect, so a failure here is not worth interrupting the others.
export async function catchUpAll(options: CatchUpOptions): Promise<void> {
  const loaded = Object.keys(chat().messages)
  let next = 0

  async function worker() {
    while (next < loaded.length) {
      const conversationId = loaded[next++]
      try {
        await catchUp(conversationId, options)
      } catch {
        // See above.
      }
    }
  }

  await Promise.all(Array.from({ length: Math.min(CONCURRENT_CONVERSATIONS, loaded.length) }, worker))
}

async function catchUpOnce(conversationId: string, { onTooFarBehind }: CatchUpOptions): Promise<void> {
  const held = chat().messages[conversationId] ?? []
  let newest = held[held.length - 1]?.id

  // A conversation that was loaded but is empty has no message to continue from; whatever was sent
  // during the gap is its whole history, so it is simply loaded again.
  if (newest === undefined) {
    onTooFarBehind(conversationId)
    return
  }

  for (let page = 0; page < CATCH_UP_MAX_PAGES; page++) {
    const loaded = await fetchMessages(conversationId, { after: newest })
    if (loaded.length < HISTORY_PAGE_SIZE) return

    newest = loaded[loaded.length - 1].id
  }

  chat().clearMessages(conversationId)
  onTooFarBehind(conversationId)
}
