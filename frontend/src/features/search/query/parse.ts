import { wordsOf } from './normalize'

// What a person typed, as something a message can be tested against. The syntax is the one the server's search had:
//
//   meeting notes    every word must be present, in any order and any case
//   meet             as the last word typed, it matches from its start (meeting, meets)
//   "exact phrase"   the words together, in that order
//   -word            the word must not be present
//   a or b           either
//
// Only a plain word that the text ends in can be unfinished, so a word after a minus or after "or" has to be complete, and so does
// anything followed by a quote or a mark. "or" with nothing after it is a word like any other.

// One thing to find, or to keep out: a word, or a run of words that must stand together.
export interface Term {
  words: string[]
  // The last word may be the start of a longer one.
  prefix: boolean
  negated: boolean
}

// Alternatives: the group holds when any one of its terms does. A query holds when every group does.
export type Group = Term[]

export interface Query {
  groups: Group[]
}

type Piece = { kind: 'term'; text: string; quoted: boolean; negated: boolean } | { kind: 'or' }

const ENDS_IN_A_WORD = /[\p{L}\p{N}_]$/u

// The pieces of the input: quoted phrases, words (with a leading minus where there is one), and the word "or".
function piecesOf(input: string): Piece[] {
  const pieces: Piece[] = []
  let at = 0

  while (at < input.length) {
    if (/\s/.test(input[at])) {
      at++
      continue
    }

    let negated = false
    if (input[at] === '-' && at + 1 < input.length && !/\s/.test(input[at + 1])) {
      negated = true
      at++
    }

    if (input[at] === '"') {
      const close = input.indexOf('"', at + 1)
      const end = close === -1 ? input.length : close
      pieces.push({ kind: 'term', text: input.slice(at + 1, end), quoted: true, negated })
      at = close === -1 ? input.length : close + 1
      continue
    }

    let end = at
    while (end < input.length && !/\s/.test(input[end])) end++
    const text = input.slice(at, end)
    at = end

    if (!negated && text.toLowerCase() === 'or') pieces.push({ kind: 'or' })
    else pieces.push({ kind: 'term', text, quoted: false, negated })
  }

  return pieces
}

// The query, or null when there is nothing to look for: no words, or only words to keep out.
export function parseQuery(input: string): Query | null {
  const pieces = piecesOf(input.trim())
  const groups: Group[] = []
  let joinNext = false
  let last: { term: Term; alone: boolean } | null = null

  pieces.forEach((piece, index) => {
    // "or" with nothing after it is a word of its own, and the last one typed.
    const dangling = piece.kind === 'or' && index === pieces.length - 1
    const resolved: Piece = dangling ? { kind: 'term', text: 'or', quoted: false, negated: false } : piece

    if (resolved.kind === 'or') {
      joinNext = groups.length > 0
      last = null
      return
    }

    // A piece that is only punctuation has no words and says nothing, and ends the text in a mark.
    const words = wordsOf(resolved.text)
    if (words.length === 0) {
      last = null
      return
    }

    const term: Term = { words, prefix: false, negated: resolved.negated }
    const joined = joinNext && groups.length > 0
    if (joined) groups[groups.length - 1].push(term)
    else groups.push([term])

    joinNext = false
    const unfinished = !resolved.quoted && !resolved.negated && ENDS_IN_A_WORD.test(resolved.text)
    last = unfinished ? { term, alone: !joined } : null
  })

  if (!groups.some((group) => group.some((term) => !term.negated))) return null

  // Only the last thing typed can be unfinished, and only when nothing is joined to it by "or".
  const unfinished = last as { term: Term; alone: boolean } | null
  if (unfinished?.alone) unfinished.term.prefix = true

  return { groups }
}
