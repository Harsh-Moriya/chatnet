import { infiniteQueryOptions, useInfiniteQuery } from '@tanstack/react-query'
import { olderCursor } from '../history'
import { fetchMessages } from './chatApi'

// How a conversation's history is loaded into the chat store: the latest page first, then older
// ones on request (fetchNextPage). The pages themselves are only a record of what was asked for; the
// messages are read from the store, which fetchMessages fills.
//
// The query never refreshes itself (staleTime: Infinity). The store is kept current by the pushes
// and, after a connection drop, by the catch-up (catchUp.ts), so there is nothing for a refresh to
// add. It would also be costly: an infinite query refetches every page it holds, one after the
// other, whenever it goes stale and the window regains focus or the conversation is reopened, so a
// reader who had scrolled back through ten pages would send ten requests each time. A refetch is
// still available on purpose (the "Try again" button) and a reset (catch-up that fell too far
// behind) still reloads from the latest page.
export function messagesQueryOptions(conversationId: string) {
  return infiniteQueryOptions({
    queryKey: ['messages', conversationId],
    // The first page has no cursor: it is the latest one.
    initialPageParam: undefined as string | undefined,
    queryFn: ({ pageParam, signal }) => fetchMessages(conversationId, { before: pageParam, signal }),
    getNextPageParam: olderCursor,
    staleTime: Infinity,
  })
}

export function useMessages(conversationId: string) {
  return useInfiniteQuery(messagesQueryOptions(conversationId))
}
