import { tokenize } from './normalize'
import type { Query, Term } from './parse'

// A piece of a snippet: text that matched the search, or the text around it. The pieces are drawn one by one, so nothing in a message
// is ever read as markup.
export interface SnippetPart {
  text: string
  match: boolean
}

// How much of the text around the first match is shown, in characters: a little before it, and more after, where the reader goes on.
const BEFORE = 40
const AFTER = 120

// Whether a word of the text is one the search looked for. Every word of every term to find counts, not only where they stand
// together: the point is to show the reader where their words are.
function isSought(word: string, terms: Term[]): boolean {
  return terms.some((term) =>
    term.words.some((wanted, at) => word === wanted || (term.prefix && at === term.words.length - 1 && word.startsWith(wanted))),
  )
}

// The text around the first place the search matched, cut to a line or two, with the matching words picked out. Words the search keeps
// out are not highlighted. A text with nothing to pick out is shown from its start.
export function snippetOf(text: string, query: Query): SnippetPart[] {
  const sought = query.groups.flat().filter((term) => !term.negated)
  const matches = tokenize(text).filter((token) => isSought(token.word, sought))

  const first = matches[0]
  const from = first ? Math.max(0, first.start - BEFORE) : 0
  const to = Math.min(text.length, (first ? first.start : 0) + AFTER)

  const parts: SnippetPart[] = []
  const push = (piece: string, match: boolean) => {
    if (piece !== '') parts.push({ text: piece.replace(/\s+/g, ' '), match })
  }

  let at = from
  push(from > 0 ? '…' : '', false)

  for (const token of matches) {
    if (token.end <= from || token.start >= to) continue

    push(text.slice(at, Math.max(at, token.start)), false)
    push(text.slice(Math.max(at, token.start), Math.min(token.end, to)), true)
    at = Math.min(token.end, to)
  }

  push(text.slice(at, to), false)
  push(to < text.length ? '…' : '', false)

  return parts
}
