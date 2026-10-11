import { useQuery } from '@tanstack/react-query'
import { fetchConversations } from './chatApi'

// Loads the sidebar list. The fetch puts the result in the chat store, which is what components
// read; the query is here for its loading and error state and for caching and refetching.
export function useConversations() {
  return useQuery({
    queryKey: ['conversations'],
    queryFn: ({ signal }) => fetchConversations(signal),
  })
}
