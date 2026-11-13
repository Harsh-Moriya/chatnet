import type { Query, Term } from './parse'

// Whether a text (as its words, in order) holds one term: the words standing together, the last of them allowed to be the start of a
// longer word when the term says so.
export function termMatches(words: readonly string[], term: Term): boolean {
  const length = term.words.length

  for (let from = 0; from + length <= words.length; from++) {
    let together = true

    for (let at = 0; at < length; at++) {
      const word = words[from + at]
      const wanted = term.words[at]
      const unfinished = term.prefix && at === length - 1

      if (word !== wanted && !(unfinished && word.startsWith(wanted))) {
        together = false
        break
      }
    }

    if (together) return true
  }

  return false
}

// Whether a text satisfies the whole query: every group holds, and a group holds when any of its terms does (a term to keep out
// holds when the text lacks it).
export function queryHolds(words: readonly string[], query: Query): boolean {
  return query.groups.every((group) => group.some((term) => (term.negated ? !termMatches(words, term) : termMatches(words, term))))
}
