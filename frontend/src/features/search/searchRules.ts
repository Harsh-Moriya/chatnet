// The rules for searching messages, as plain functions so they can be tested without a browser. The components only apply their
// answers.

// A search shorter or longer than this (after trimming) is not run.
export const MIN_TERM_LENGTH = 2
export const MAX_TERM_LENGTH = 100

// How many hits are read for a page. A scan that ran out of messages before it had a page says so by having no next position.
export const SEARCH_PAGE_SIZE = 20

// What to search for, given what is in the box: the trimmed text, or null while it is too short or too long to be a search.
export function searchTermFrom(input: string): string | null {
  const term = input.trim()
  return term.length >= MIN_TERM_LENGTH && term.length <= MAX_TERM_LENGTH ? term : null
}
