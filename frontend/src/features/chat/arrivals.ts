// Which of the items of a conversation are new arrivals, to be drawn with a short entrance. `previous` is the keys the reader was looking at,
// or null when the conversation has only just been opened. Only what comes after the last item that was already there counts: older
// messages added above (a page of history), a window that replaced what was shown and a re-sort are not arrivals, and neither is the
// first time a conversation is drawn, so opening one does not animate its history.
export function arrivingKeys(previous: readonly string[] | null, current: readonly string[]): ReadonlySet<string> {
  if (previous === null || previous.length === 0) return NONE

  const lastKnown = current.indexOf(previous[previous.length - 1])
  if (lastKnown === -1) return NONE

  const known = new Set(previous)
  return new Set(current.slice(lastKnown + 1).filter((key) => !known.has(key)))
}

const NONE: ReadonlySet<string> = new Set()
