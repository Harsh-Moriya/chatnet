import { useQuery } from '@tanstack/react-query'
import { fetchMessages } from './chatApi'

// Loads the latest messages of one conversation into the chat store. The key starts with
// 'messages' so a reconnect can refresh every conversation's history in one call.
export function useMessages(conversationId: string) {
  return useQuery({
    queryKey: ['messages', conversationId],
    queryFn: ({ signal }) => fetchMessages(conversationId, signal),
  })
}
