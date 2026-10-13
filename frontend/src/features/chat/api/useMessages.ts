import { useInfiniteQuery } from '@tanstack/react-query'
import { olderCursor } from '../history'
import { fetchMessages } from './chatApi'

// Loads a conversation's history into the chat store a page at a time: the latest page first, then
// older ones on request (fetchNextPage). The pages themselves are only a record of what was asked
// for; the messages are read from the store, which fetchMessages fills. The key starts with
// 'messages' so a reconnect can refresh every conversation's history in one call.
export function useMessages(conversationId: string) {
  return useInfiniteQuery({
    queryKey: ['messages', conversationId],
    // The first page has no cursor: it is the latest one.
    initialPageParam: undefined as string | undefined,
    queryFn: ({ pageParam, signal }) => fetchMessages(conversationId, { before: pageParam, signal }),
    getNextPageParam: olderCursor,
  })
}
