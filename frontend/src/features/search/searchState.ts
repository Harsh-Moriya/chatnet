// What the search shows, decided from what is known, as a plain function so it can be tested without a browser. The components draw
// what it says.

export type SearchView =
  // Not enough typed to search.
  | { kind: 'short' }
  // Enough typed, but nothing to look for in it (only words to keep out, or only marks).
  | { kind: 'nothing' }
  // This browser cannot keep the messages, so there is nothing to search.
  | { kind: 'unavailable' }
  | { kind: 'searching' }
  | { kind: 'failed' }
  // Older messages are still being read into the store, so what was found, or not found, is of what is there so far.
  | { kind: 'empty'; stillReading: boolean }
  | { kind: 'hits'; stillReading: boolean }

export interface SearchFacts {
  // The text in the box when it is long enough to search, or null.
  typed: string | null
  // Whether that text holds something to look for.
  hasTerms: boolean
  unavailable: boolean
  // How many conversations have older messages that are not in the store yet.
  historyPending: number
  hitCount: number
  failed: boolean
  // The search for what is in the box has finished.
  settled: boolean
}

export function viewOf(facts: SearchFacts): SearchView {
  if (facts.unavailable) return { kind: 'unavailable' }
  if (facts.typed === null) return { kind: 'short' }
  if (!facts.hasTerms) return { kind: 'nothing' }

  const stillReading = facts.historyPending > 0

  if (facts.hitCount > 0) return { kind: 'hits', stillReading }
  if (facts.failed) return { kind: 'failed' }

  return facts.settled ? { kind: 'empty', stillReading } : { kind: 'searching' }
}
