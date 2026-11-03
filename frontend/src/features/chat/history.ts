import type { Message } from './types'

// The rules for reading further back in a conversation, as plain functions so they can be tested
// without a browser. The components only apply their answers.

// How many messages the server returns per page. A shorter page means the start of the history was
// reached; the server never returns more.
export const HISTORY_PAGE_SIZE = 50

// One direction of reading further in a conversation: whether there is more, whether a request is out, and why the last one failed.
// While an error is set nothing is requested by itself; the reader's "Try again" asks once more.
export interface ReadingMore {
  hasMore: boolean
  isLoading: boolean
  error: string | null
  onLoad: () => void
}

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

export interface FollowState {
  atBottom: boolean
  inWindow: boolean
  justSent: boolean
}

// Whether the list shows its end when its content changes. A reader at the bottom follows new messages, one who scrolled away is
// left where they are, and a message the reader just sent always comes into view. A window of the history never follows: the
// bottom of a window is not the bottom of the conversation, and following it would undo a jump to a message.
export function followsEnd({ atBottom, inWindow, justSent }: FollowState): boolean {
  return justSent || (atBottom && !inWindow)
}

// Whether the last item of the list is a message the reader has just sent: a pending message that was not already the last item.
// A failed message waits for "Retry" at the end for as long as the reader likes, and being there is not being sent again.
export function justSent(previousLastKey: string | undefined, last: { kind: string; key: string } | undefined): boolean {
  return last?.kind === 'pending' && last.key !== previousLastKey
}

// Whether leaving a window shows the newest messages from their end. The jump to the latest messages and sending from a window
// drop what was held, which empties the list until the latest page arrives, and the reader expects to land at the end. A window
// that merely reached the newest message by reading on keeps its messages, and the reader stays where they are.
export function returnsToEnd(state: { wasInWindow: boolean; inWindow: boolean; messageCount: number }): boolean {
  return state.wasInWindow && !state.inWindow && state.messageCount === 0
}
