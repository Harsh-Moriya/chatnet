import { useQuery } from '@tanstack/react-query'
import { fetchMembers } from './chatApi'

// Loads who is in a conversation and how far each has read into the chat store. The key starts with
// 'members' so a reconnect can refresh every conversation's members in one call.
export function useMembers(conversationId: string) {
  return useQuery({
    queryKey: ['members', conversationId],
    queryFn: ({ signal }) => fetchMembers(conversationId, signal),
  })
}
