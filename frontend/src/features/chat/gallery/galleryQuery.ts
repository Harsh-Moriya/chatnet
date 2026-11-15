import { infiniteQueryOptions } from '@tanstack/react-query'
import { requestMessageFiles } from '../api/chatApi'
import type { Message } from '../types'

// How many messages the server returns per page of the gallery's list. A shorter page is the last.
export const FILES_PAGE_SIZE = 50

// The cursor for the page just older than the one loaded (newest message first), or undefined when this was the last. A full page may
// still be the last one, in which case the next request returns an empty page and paging stops there.
export function nextFilesCursor(page: readonly Message[]): string | undefined {
  return page.length >= FILES_PAGE_SIZE ? page[page.length - 1].id : undefined
}

// The list of a conversation's messages with files, read newest first a page at a time. Every opening starts from the newest page
// (nothing is kept once the gallery and the viewer over it are closed), so it shows what was sent since it was last open. It never
// refreshes itself: an infinite query refetches every page it holds, and a person who had scrolled back through the history would send
// one request per page each time the window regained focus.
export function galleryQueryOptions(conversationId: string) {
  return infiniteQueryOptions({
    queryKey: ['gallery', conversationId],
    initialPageParam: undefined as string | undefined,
    queryFn: ({ pageParam, signal }) => requestMessageFiles(conversationId, { before: pageParam, signal }),
    getNextPageParam: nextFilesCursor,
    staleTime: 0,
    gcTime: 0,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
  })
}
