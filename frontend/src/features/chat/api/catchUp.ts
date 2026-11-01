import { HISTORY_PAGE_SIZE } from '../history'
import { useChatStore } from '../store/chatStore'
import { fetchMessages, requestMessages } from './chatApi'

// What to do after the live connection came back from a drop. Pushes sent while it was down are
// lost: new messages, and also edits and deletions, which do not make a message newer, so a question
// of the kind "what came after the newest message I hold" never returns them. Each conversation that
// has been loaded therefore asks for its latest page first. A page that reaches back to the newest
// message held shows both what was sent meanwhile and what became of the last messages held, and is
// all it takes after a short absence. After a longer one the page is not used, and the conversation
// asks for everything after the newest message it holds, a page at a time, until a page comes back
// short, and then looks back over the messages just before the gap. The server's history is the
// source of truth, and the store merges what it returns by id and keeps the later version of each
// message, so running this twice, or racing a push, is harmless. This is a reaction to the
// connection coming back, not to a message arriving.
//
// What it does not reach: an edit or a deletion of a message older than the last page held, in a
// tab that was away for longer than a page of messages. The reader sees it after a reload.

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
  const newestHeld = held[held.length - 1]?.id

  // A conversation that was loaded but is empty has no message to continue from; whatever was sent
  // during the gap is its whole history, so it is simply loaded again.
  if (newestHeld === undefined) {
    onTooFarBehind(conversationId)
    return
  }

  // The latest page joins up with what is held when it contains the newest message held (or is the whole
  // history, being short). Only then does it go into the store: a page that starts after the newest message
  // held would put a hole in the middle of the history, which nothing would fill if the requests that follow fail.
  const latest = await requestMessages(conversationId)
  if (latest.length < HISTORY_PAGE_SIZE || latest.some((m) => m.id === newestHeld)) {
    chat().seedMessages(conversationId, latest)
    return
  }

  let cursor = newestHeld
  let firstNew: string | undefined
  for (let page = 0; page < CATCH_UP_MAX_PAGES; page++) {
    const loaded = await fetchMessages(conversationId, { after: cursor })
    firstNew ??= loaded[0]?.id

    if (loaded.length < HISTORY_PAGE_SIZE) {
      // The messages just before the first new one are the last that were held; they may have been edited or deleted.
      if (firstNew !== undefined) await fetchMessages(conversationId, { before: firstNew })
      return
    }

    cursor = loaded[loaded.length - 1].id
  }

  chat().clearMessages(conversationId)
  onTooFarBehind(conversationId)
}
