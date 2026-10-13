import type { Message } from './types'

// The rules for reading further back in a conversation, as plain functions so they can be tested
// without a browser. The components only apply their answers.

// How many messages the server returns per page. A shorter page means the start of the history was
// reached; the server never returns more.
export const HISTORY_PAGE_SIZE = 50

// The cursor for the page of older messages, given the page just loaded (oldest message first), or
// undefined when there is nothing older. A full page may still be the last one, in which case the
// next request returns an empty page and paging stops there.
export function olderCursor(page: Message[]): string | undefined {
  return page.length >= HISTORY_PAGE_SIZE ? page[0].id : undefined
}

// Older messages were added above what the reader was looking at when the first message of the
// conversation changed. A new message at the bottom, or the first load of an empty list, is not one.
export function wasPrepended(previousFirstId: string | undefined, firstId: string | undefined): boolean {
  return previousFirstId !== undefined && firstId !== undefined && previousFirstId !== firstId
}

export interface ScrollMeasure {
  scrollTop: number
  scrollHeight: number
}

// Where to scroll to after content was added above the viewport so that the reader keeps looking at
// the same message: moved down by exactly the height that was added.
export function scrollTopAfterPrepend(before: ScrollMeasure, scrollHeightNow: number): number {
  return before.scrollTop + (scrollHeightNow - before.scrollHeight)
}
