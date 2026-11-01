import type { SearchHit } from './types'

// The rules for searching messages, as plain functions so they can be tested without a browser. The components only apply
// their answers.

// The server refuses a search shorter or longer than this (after trimming), so the box does not send one.
export const MIN_TERM_LENGTH = 2
export const MAX_TERM_LENGTH = 100

// How many hits the server returns per page. A shorter page means the last one was reached; the server never returns more.
export const SEARCH_PAGE_SIZE = 20

// What to ask the server for, given what is in the box: the trimmed text, or null while it is too short or too long to be
// a search.
export function searchTermFrom(input: string): string | null {
  const term = input.trim()
  return term.length >= MIN_TERM_LENGTH && term.length <= MAX_TERM_LENGTH ? term : null
}

// The cursor for the next page of hits, given the page just loaded (newest first), or undefined when there are no more. A
// full page may still be the last one, in which case the next request returns an empty page and paging stops there.
export function nextSearchCursor(page: SearchHit[]): string | undefined {
  return page.length >= SEARCH_PAGE_SIZE ? page[page.length - 1].messageId : undefined
}
