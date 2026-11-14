// When the site is updated, a tab that was open before still asks for script files by their old names, and the server answers with the
// page instead (the files are gone). The import fails, and so does whatever needed it: a send, a dialog. Loading the page again fetches
// the new names. This is done once, and not again within a short time, so a file that is really missing cannot cause a loop.

export const RELOAD_KEY = 'chatnet.stale-chunk-reload'
export const RELOAD_WINDOW_MS = 10_000

export interface StaleChunkDeps {
  // A function, because merely reaching the storage throws in a browser that blocks it.
  storage: () => Pick<Storage, 'getItem' | 'setItem'>
  now: () => number
  reload: () => void
}

// Returns whether it took the event: the page is being reloaded and the failed import is not reported as an error.
export function reloadForStaleChunk(event: Event, { storage, now, reload }: StaleChunkDeps): boolean {
  try {
    const kept = storage()
    const last = Number(kept.getItem(RELOAD_KEY))
    if (Number.isFinite(last) && last > 0 && now() - last < RELOAD_WINDOW_MS) return false

    // Written first: a browser that cannot keep it must not reload, because it could not remember that it did.
    kept.setItem(RELOAD_KEY, String(now()))
  } catch {
    return false
  }

  event.preventDefault()
  reload()
  return true
}
