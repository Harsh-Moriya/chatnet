import { openMessagesQuietly } from '../../chat/api/openWire'
import { fetchChanges, fetchHistoryPage, isCursorAhead } from '../api/changesApi'
import { indexedDbLocalStore } from '../store/indexedDbLocalStore'
import { syncAll } from './engine'
import { withReadLock } from './lock'
import type { Listed } from './plan'
import { useSyncStatus } from './status'

// Starts reading into the local store, wired to the real server, the real opener and IndexedDB. One run at a time in this tab: a request
// that comes while one is going (the list moved again) is remembered, and runs when the first one ends with the list as it is by then.

let flight: Promise<void> | null = null
let waiting: Listed[] | null = null

async function runOnce(userId: string, listed: Listed[], signal: AbortSignal): Promise<void> {
  try {
    await indexedDbLocalStore.prepare(userId)
  } catch {
    // This browser cannot keep the store. Search says so; nothing else changes.
    useSyncStatus.getState().markUnavailable()
    return
  }

  try {
    await withReadLock(userId, () =>
      syncAll(
        {
          store: indexedDbLocalStore,
          userId,
          history: fetchHistoryPage,
          changes: fetchChanges,
          open: openMessagesQuietly,
          cursorAhead: isCursorAhead,
          onHistoryPending: (count) => useSyncStatus.getState().setHistoryPending(count),
          signal,
        },
        listed,
      ),
    )
  } catch (error) {
    // A run that stopped because the person signed out or left the chat is not a failure. Anything else is tried again by the next
    // run, which reads from where the store stopped.
    if (!signal.aborted) console.warn('Reading messages into the local store failed:', error instanceof Error ? error.message : 'unknown error')
  }
}

export function readIntoStore(userId: string, listed: Listed[], signal: AbortSignal): Promise<void> {
  if (flight) {
    waiting = listed
    return flight
  }

  const run = (async () => {
    let next: Listed[] | null = listed

    while (next && !signal.aborted) {
      waiting = null
      await runOnce(userId, next, signal)
      next = waiting
    }
  })()

  // Cleared after the read is over, and not from inside it: a read that has nothing to do ends before this function has even
  // returned, and clearing it from inside would be undone by the line that remembers it.
  flight = run
  void run.finally(() => {
    if (flight === run) {
      flight = null
      waiting = null
    }
  })

  return run
}
